using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ExpertMouse;

internal static class Native
{
    internal const uint DeviceName = 0x20000007;
    internal const uint PreparsedData = 0x20000005;
    internal const uint Input = 0x10000003;
    internal const int HidSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceList
    {
        internal nint Handle;
        internal uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Registration
    {
        internal ushort UsagePage;
        internal ushort Usage;
        internal uint Flags;
        internal nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Header
    {
        internal uint Type;
        internal uint Size;
        internal nint Device;
        internal nint WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Caps
    {
        internal ushort Usage;
        internal ushort UsagePage;
        internal ushort InputReportByteLength;
        internal ushort OutputReportByteLength;
        internal ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        internal ushort[] Reserved;
        internal ushort NumberLinkCollectionNodes;
        internal ushort NumberInputButtonCaps;
        internal ushort NumberInputValueCaps;
        internal ushort NumberInputDataIndices;
        internal ushort NumberOutputButtonCaps;
        internal ushort NumberOutputValueCaps;
        internal ushort NumberOutputDataIndices;
        internal ushort NumberFeatureButtonCaps;
        internal ushort NumberFeatureValueCaps;
        internal ushort NumberFeatureDataIndices;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputDeviceList(
        [In, Out] DeviceList[]? devices, ref uint count, uint entrySize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    internal static extern uint GetRawInputDeviceInfo(
        nint device, uint command, nint data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterRawInputDevices(
        Registration[] devices, uint count, uint entrySize);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputData(
        nint input, uint command, nint data, ref uint size, uint headerSize);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(
        string path, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [DllImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidD_GetPreparsedData(SafeFileHandle device, out nint data);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool HidD_FreePreparsedData(nint data);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetCaps(nint data, out Caps caps);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetButtonCaps(int type, [Out] byte[] caps, ref ushort count, nint data);

    [DllImport("hid.dll")]
    internal static extern int HidP_GetValueCaps(int type, [Out] byte[] caps, ref ushort count, nint data);
}
