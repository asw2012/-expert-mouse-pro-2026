using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ExpertMouse;

internal sealed record UsbTarget(string CaptureDevice, ushort Bus, ushort Address,
    string HubPath, uint Port);

internal static class UsbCapture
{
    internal const uint SetupBuffer = 0x226000;
    internal const uint StartFiltering = 0x22E004;
    internal const uint StopFiltering = 0x22E008;
    private const uint GetHub = 0x22200C;
    internal const uint SetSnapLength = 0x226010;
    private const uint NodeInformation = 0x220408;
    private const uint ConnectionInformation = 0x220448;
    private const uint ConnectionName = 0x220414;

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string? name, [Out] char[] target, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code,
        byte[]? input, uint inputSize, [Out] byte[]? output, uint outputSize,
        out uint returned, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadFile(SafeFileHandle handle, [Out] byte[] buffer,
        uint size, out uint read, nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CancelIoEx(SafeFileHandle handle, nint overlapped);

    internal static SafeFileHandle Open(string path, uint access, uint share = 0)
    {
        var handle = Native.CreateFile(path, access, share, 0, 3, 0, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, $"Open {path}. Capture requires administrator access; " +
                "another USBPcap capture on the same hub must be closed first.");
        }
        return handle;
    }

    internal static uint Control(SafeFileHandle handle, uint code, byte[]? input = null, byte[]? output = null)
    {
        if (!DeviceIoControl(handle, code, input, (uint)(input?.Length ?? 0),
                output, (uint)(output?.Length ?? 0), out var returned, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"USB control request 0x{code:X8}");
        return returned;
    }

    internal static List<UsbTarget> FindTargets()
    {
        var names = new char[65536];
        var length = QueryDosDevice(null, names, (uint)names.Length);
        if (length == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "List USB capture interfaces");
        var targets = new List<UsbTarget>();
        foreach (var name in new string(names, 0, checked((int)length)).Split('\0',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (!name.StartsWith("USBPcap", StringComparison.Ordinal) ||
                !ushort.TryParse(name.AsSpan(7), out var bus) || bus == 0)
                continue;
            var path = @"\\.\" + name;
            using var capture = Open(path, 0);
            var bytes = new byte[8192];
            var read = Control(capture, GetHub, output: bytes);
            var hub = ReadName(bytes, read, 0);
            EnumerateHub(path, bus, hub, targets, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
        }
        return targets;
    }

    private static void EnumerateHub(string capturePath, ushort bus, string hub,
        List<UsbTarget> targets, HashSet<string> visited, int depth)
    {
        if (depth > 16 || !visited.Add(hub))
            throw new InvalidOperationException("Invalid or cyclic USB hub topology.");
        using var handle = Open(hub, 0x40000000, 2);
        var node = new byte[76];
        var read = Control(handle, NodeInformation, node, node);
        if (read < 7)
            throw new InvalidOperationException("Truncated USB hub information.");
        for (uint port = 1; port <= node[6]; port++)
        {
            var connection = ReadConnection(handle, port);
            if (BinaryPrimitives.ReadUInt32LittleEndian(connection.AsSpan(31)) != 1)
                continue;
            if (IsExpert(connection))
            {
                var address = BinaryPrimitives.ReadUInt16LittleEndian(connection.AsSpan(25));
                if (address is 0 or > 127)
                    throw new InvalidOperationException($"Invalid trackball USB address {address}.");
                targets.Add(new(capturePath, bus, address, hub, port));
            }
            if (connection[24] != 0)
            {
                var name = new byte[8192];
                BinaryPrimitives.WriteUInt32LittleEndian(name, port);
                read = Control(handle, ConnectionName, name, name);
                EnumerateHub(capturePath, bus, ReadName(name, read, 8), targets, visited, depth + 1);
            }
        }
    }

    private static byte[] ReadConnection(SafeFileHandle hub, uint port)
    {
        var connection = new byte[4096];
        BinaryPrimitives.WriteUInt32LittleEndian(connection, port);
        if (Control(hub, ConnectionInformation, connection, connection) < 35)
            throw new InvalidOperationException("Truncated USB connection information.");
        return connection;
    }

    internal static bool IsExpert(ReadOnlySpan<byte> connection) =>
        connection.Length >= 35 &&
        BinaryPrimitives.ReadUInt32LittleEndian(connection[31..]) == 1 &&
        BinaryPrimitives.ReadUInt16LittleEndian(connection[12..]) == 0x047D &&
        BinaryPrimitives.ReadUInt16LittleEndian(connection[14..]) == 0x1002;

    internal static void VerifyTarget(UsbTarget target)
    {
        using var hub = Open(target.HubPath, 0x40000000, 2);
        var connection = ReadConnection(hub, target.Port);
        if (!IsExpert(connection) ||
            BinaryPrimitives.ReadUInt16LittleEndian(connection.AsSpan(25)) != target.Address)
            throw new InvalidOperationException("The trackball was disconnected or its USB address changed. " +
                "Close this window, reconnect it, and start a new capture.");
    }

    private static string ReadName(byte[] bytes, uint size, int offset)
    {
        if (size > bytes.Length || size <= offset || (size - offset) % 2 != 0)
            throw new InvalidOperationException("Invalid USB hub name length.");
        var name = Encoding.Unicode.GetString(bytes, offset, checked((int)size) - offset).TrimEnd('\0');
        if (string.IsNullOrWhiteSpace(name) || name.Contains('\0'))
            throw new InvalidOperationException("Invalid USB hub name.");
        if (name.StartsWith(@"\??\", StringComparison.Ordinal))
            return @"\\.\" + name[4..];
        return name.StartsWith('\\') ? name : @"\\.\" + name;
    }

    internal static byte[] AddressFilter(ushort address)
    {
        if (address is 0 or > 127)
            throw new InvalidOperationException("Capture must select exactly one assigned USB address (1..127).");
        // The packed USBPcap filter contains four address bitmaps and a filterAll byte.
        var filter = new byte[17];
        var word = address / 32 * 4;
        BinaryPrimitives.WriteUInt32LittleEndian(filter.AsSpan(word), 1u << (address % 32));
        return filter;
    }
}
