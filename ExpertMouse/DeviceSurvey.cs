using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ExpertMouse;

internal sealed record Capability(
    string Kind, ushort UsagePage, byte ReportId, ushort UsageMin, ushort UsageMax, string RawHex);

internal sealed record DeviceDescription(
    string Path, uint RawInputType, ushort UsagePage, ushort Usage,
    ushort InputBytes, ushort OutputBytes, ushort FeatureBytes,
    IReadOnlyList<Capability> Capabilities);

internal sealed record ConnectedDevice(nint Handle, DeviceDescription Description);

internal static class DeviceSurvey
{
    internal static bool IsTarget(string path) =>
        path.Contains("VID_047D&PID_1002", StringComparison.OrdinalIgnoreCase);

    internal static List<ConnectedDevice> Find()
    {
        var result = new List<ConnectedDevice>();
        foreach (var device in Enumerate())
        {
            var path = GetName(device.Handle);
            if (!IsTarget(path))
                continue;
            using var handle = Native.CreateFile(path, 0, 3, 0, 3, 0, 0);
            if (handle.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Open Expert Mouse for metadata");
            if (!Native.HidD_GetPreparsedData(handle, out var data))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Read Expert Mouse HID metadata");
            try
            {
                CheckHid(Native.HidP_GetCaps(data, out var caps), "Read HID capabilities");
                var capabilities = new List<Capability>();
                ReadCapabilities(data, 0, caps.NumberInputButtonCaps, true, "InputButton", capabilities);
                ReadCapabilities(data, 0, caps.NumberInputValueCaps, false, "InputValue", capabilities);
                ReadCapabilities(data, 1, caps.NumberOutputButtonCaps, true, "OutputButton", capabilities);
                ReadCapabilities(data, 1, caps.NumberOutputValueCaps, false, "OutputValue", capabilities);
                ReadCapabilities(data, 2, caps.NumberFeatureButtonCaps, true, "FeatureButton", capabilities);
                ReadCapabilities(data, 2, caps.NumberFeatureValueCaps, false, "FeatureValue", capabilities);
                result.Add(new(device.Handle, new(path, device.Type, caps.UsagePage, caps.Usage,
                    caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength,
                    capabilities)));
            }
            finally
            {
                if (!Native.HidD_FreePreparsedData(data))
                    throw new InvalidOperationException("Could not release HID metadata.");
            }
        }
        return result;
    }

    internal static string[] MousePaths() =>
        Enumerate().Where(d => d.Type == 0).Select(d => GetName(d.Handle)).ToArray();

    internal static bool IsSoleExpertMouse(IReadOnlyList<string> paths) =>
        paths.Count == 1 && IsTarget(paths[0]);

    private static Native.DeviceList[] Enumerate()
    {
        uint count = 0;
        var entrySize = (uint)Marshal.SizeOf<Native.DeviceList>();
        Check(Native.GetRawInputDeviceList(null, ref count, entrySize), "List input devices");
        if (count == 0)
            return [];
        var devices = new Native.DeviceList[count];
        var read = Native.GetRawInputDeviceList(devices, ref count, entrySize);
        Check(read, "Read input device list");
        return devices.Take(checked((int)read)).ToArray();
    }

    private static void ReadCapabilities(nint data, int type, ushort count, bool buttons,
        string kind, List<Capability> result)
    {
        if (count == 0)
            return;
        const int size = 72;
        var bytes = new byte[count * size];
        var status = buttons
            ? Native.HidP_GetButtonCaps(type, bytes, ref count, data)
            : Native.HidP_GetValueCaps(type, bytes, ref count, data);
        CheckHid(status, $"Read {kind} capabilities");
        for (var i = 0; i < count; i++)
        {
            var cap = bytes.AsSpan(i * size, size);
            var min = BitConverter.ToUInt16(cap[56..]);
            var max = cap[12] != 0 ? BitConverter.ToUInt16(cap[58..]) : min;
            result.Add(new(kind, BitConverter.ToUInt16(cap), cap[2], min, max, Convert.ToHexString(cap)));
        }
    }

    private static string GetName(nint handle)
    {
        uint chars = 0;
        Check(Native.GetRawInputDeviceInfo(handle, Native.DeviceName, 0, ref chars), "Size device name");
        var buffer = Marshal.AllocHGlobal(checked((int)(chars + 1) * 2));
        try
        {
            Check(Native.GetRawInputDeviceInfo(handle, Native.DeviceName, buffer, ref chars), "Read device name");
            return Marshal.PtrToStringUni(buffer)
                ?? throw new InvalidOperationException("Input device returned no name.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void Check(uint result, string operation)
    {
        if (result == uint.MaxValue)
            throw new Win32Exception(Marshal.GetLastWin32Error(), operation);
    }

    private static void CheckHid(int status, string operation)
    {
        if (status != Native.HidSuccess)
            throw new InvalidOperationException($"{operation}: HID status 0x{status:X8}");
    }
}
