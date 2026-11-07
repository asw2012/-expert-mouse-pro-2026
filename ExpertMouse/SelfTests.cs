using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ExpertMouse;

internal static class SelfTests
{
    internal static void Run()
    {
        Expect(Marshal.SizeOf<Native.Header>() == 24, "x64 RAWINPUTHEADER layout");
        Expect(Marshal.SizeOf<Native.DeviceList>() == 16, "x64 RAWINPUTDEVICELIST layout");
        Expect(Marshal.SizeOf<Native.Registration>() == 16, "x64 RAWINPUTDEVICE layout");
        Expect(Marshal.SizeOf<Native.Caps>() == 64, "HIDP_CAPS layout");
        Expect(DeviceSurvey.IsTarget(@"\\?\HID#vid_047d&pid_1002#abc"), "case-insensitive device match");
        Expect(!DeviceSurvey.IsTarget(@"\\?\HID#VID_047D&PID_1003#abc"), "exclude other devices");
        var targets = new HashSet<nint> { 123 };
        Expect(!CaptureForm.IsTargetRemoval(1, 123, targets), "ignore target arrival notification");
        Expect(CaptureForm.IsTargetRemoval(2, 123, targets), "detect target removal notification");
        Expect(!CaptureForm.IsTargetRemoval(2, 456, targets), "ignore unrelated device removal");

        var mouse = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(mouse.AsSpan(4), 0x0100);
        BinaryPrimitives.WriteInt16LittleEndian(mouse.AsSpan(6), -120);
        BinaryPrimitives.WriteUInt32LittleEndian(mouse.AsSpan(8), 0x400);
        BinaryPrimitives.WriteInt32LittleEndian(mouse.AsSpan(12), -3);
        var decoded = RawPacket.Decode(0, mouse);
        Expect(decoded.ButtonFlags == 0x0100 && decoded.ButtonData == -120
            && decoded.RawButtons == 0x400 && decoded.X == -3, "mouse fields and signed wheel data");
        var hid = new byte[] { 2, 0, 0, 0, 2, 0, 0, 0, 0xAA, 0xBB, 0xCC, 0xDD };
        Expect(RawPacket.Decode(2, hid).HidHex == "AABBCCDD", "multiple HID reports");
        Reject(() => RawPacket.Decode(0, new byte[23]), "truncated mouse");
        Reject(() => RawPacket.Decode(2, new byte[7]), "truncated HID");
        hid[0] = 3;
        Reject(() => RawPacket.Decode(2, hid), "oversized HID");
        Reject(() => RawPacket.Decode(2, new byte[8]), "empty HID");
        Reject(() => RawPacket.Decode(2,
            new byte[] { 255, 255, 255, 255, 255, 255, 255, 255 }), "HID size multiplication overflow");
        Reject(() => RawPacket.Decode(1, mouse), "unexpected keyboard data");
        var deviceChecks = RunDeviceNameChecks();
        var usbChecks = RunUsbChecks();
        var remapperChecks = RemapperTests.Run();
        var themeChecks = ThemeTests.Run();
        Console.WriteLine($"PASS: {17 + deviceChecks + usbChecks + remapperChecks + themeChecks} tests " +
            $"({deviceChecks} device name checks, {usbChecks} USB capture checks, " +
            $"{remapperChecks} remapper checks, {themeChecks} theme checks).");
    }

    private static int RunDeviceNameChecks()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            Expect(condition, name);
            checks++;
        }
        const string path = @"\\?\HID#VID_047D&PID_1002#test";
        uint nameSize = 1;
        var reads = 0;
        var sizeError = false;
        var readError = false;
        var emptyName = false;
        uint ReadInfo(nint device, uint command, nint data, ref uint size)
        {
            Expect(device == 123 && command == Native.DeviceName, "device name query arguments");
            if (data == 0)
            {
                size = nameSize;
                Marshal.SetLastPInvokeError(5);
                return sizeError ? uint.MaxValue : 0;
            }
            reads++;
            if (readError)
            {
                Marshal.SetLastPInvokeError(6);
                return uint.MaxValue;
            }
            Expect(size == path.Length + 1, "device name capacity is in characters");
            Marshal.Copy(((emptyName ? "" : path) + '\0').ToCharArray(), 0, data,
                emptyName ? 1 : path.Length + 1);
            return size;
        }
        Check(DeviceSurvey.GetName(123, ReadInfo) is null && reads == 0,
            "terminator-only device name is unnamed without a failing second query");
        Check(!DeviceSurvey.IsTarget(null), "unnamed device is not a target");
        Check(!DeviceSurvey.IsSoleExpertMouse([null]), "unnamed mouse alone cannot enable suppression");
        Check(!DeviceSurvey.IsSoleExpertMouse([path, null]), "unnamed second mouse still blocks suppression");
        nameSize = (uint)path.Length + 1;
        Check(DeviceSurvey.GetName(123, ReadInfo) == path && reads == 1,
            "named device path is read intact");
        emptyName = true;
        Reject(() => DeviceSurvey.GetName(123, ReadInfo), "unexpected empty device name");
        checks++;
        emptyName = false;
        nameSize = 0;
        Reject(() => DeviceSurvey.GetName(123, ReadInfo), "zero device name capacity");
        checks++;
        foreach (var failSizing in new[] { true, false })
        {
            nameSize = (uint)path.Length + 1;
            sizeError = failSizing;
            readError = !failSizing;
            var surfaced = false;
            try { DeviceSurvey.GetName(123, ReadInfo); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                var expected = failSizing ? 5 : 6;
                surfaced = ex.NativeErrorCode == expected && ex.Message.Contains($"Windows error {expected}");
            }
            Check(surfaced, "real device name errors retain the Windows error code");
        }
        return checks;
    }

    private static int RunUsbChecks()
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            Expect(condition, name);
            checks++;
        }
        void Invalid(Action action, string name)
        {
            Reject(action, name);
            checks++;
        }
        foreach (ushort address in new ushort[] { 1, 6, 31, 32, 63, 64, 95, 96, 127 })
        {
            var filter = UsbCapture.AddressFilter(address);
            Check(filter.Length == 17 && filter[16] == 0, $"packed single-device filter {address}");
            for (var candidate = 0; candidate < 128; candidate++)
            {
                var enabled = (BinaryPrimitives.ReadUInt32LittleEndian(filter.AsSpan(candidate / 32 * 4))
                    & (1u << (candidate % 32))) != 0;
                Expect(enabled == (candidate == address), $"filter {address} must not capture {candidate}");
            }
        }
        Invalid(() => UsbCapture.AddressFilter(0), "reject new-device capture");
        Invalid(() => UsbCapture.AddressFilter(128), "reject invalid USB address");
        var connection = new byte[35];
        BinaryPrimitives.WriteUInt16LittleEndian(connection.AsSpan(12), 0x047D);
        BinaryPrimitives.WriteUInt16LittleEndian(connection.AsSpan(14), 0x1002);
        BinaryPrimitives.WriteUInt32LittleEndian(connection.AsSpan(31), 1);
        Check(UsbCapture.IsExpert(connection), "USB descriptor VID/PID match");
        connection[31] = 0;
        Check(!UsbCapture.IsExpert(connection), "disconnected trackball is not a target");
        connection[31] = 1;
        connection[14] = 3;
        Check(!UsbCapture.IsExpert(connection), "reject another Kensington device");
        Check(!UsbCapture.IsExpert(new byte[34]), "reject truncated USB descriptor");

        var pcap = PcapFixture();
        var decoder = new UsbPcapDecoder(1, 6);
        var decoded = decoder.Append(pcap);
        decoder.Complete();
        Check(decoded.Count == 1 && decoded[0].Hex == "0000000001" && decoded[0].Endpoint == 0x81,
            "decode five-byte USB input payload including extra-byte state");
        Check(decoded[0].Time == DateTimeOffset.FromUnixTimeSeconds(1700000000).AddTicks(1234560),
            "preserve capture UTC microsecond timestamp");
        Check(decoder.Records == 1 && decoder.HeaderRead, "track parsed record count");
        decoder = new(1, 6);
        var fragmented = new List<UsbReport>();
        foreach (var value in pcap)
            fragmented.AddRange(decoder.Append([value]));
        decoder.Complete();
        Check(fragmented.Count == 1 && fragmented[0].Hex == decoded[0].Hex,
            "parse header and record fragmented at every byte");
        var record = pcap[24..];
        decoder = new(1, 6);
        Check(decoder.Append([.. pcap, .. record]).Count == 2, "multiple records in a single read");

        var outgoing = pcap.ToArray();
        outgoing[24 + 16 + 16] = 0;
        Check(new UsbPcapDecoder(1, 6).Append(outgoing).Count == 0, "ignore interrupt submissions");
        outgoing[24 + 16 + 16] = 1;
        outgoing[24 + 16 + 21] = 1;
        Check(new UsbPcapDecoder(1, 6).Append(outgoing).Count == 0, "ignore outgoing interrupt data");
        Invalid(() => new UsbPcapDecoder(2, 6).Append(pcap), "reject unrelated USB bus");
        Invalid(() => new UsbPcapDecoder(1, 7).Append(pcap), "reject unrelated USB address");
        var malformed = pcap.ToArray();
        malformed[20] = 1;
        Invalid(() => new UsbPcapDecoder(1, 6).Append(malformed), "reject non-USBPcap link type");
        malformed = pcap.ToArray();
        malformed[0] = 0;
        Invalid(() => new UsbPcapDecoder(1, 6).Append(malformed), "reject bad PCAP magic");
        malformed = pcap.ToArray();
        malformed[24 + 12] = 33;
        Invalid(() => new UsbPcapDecoder(1, 6).Append(malformed), "reject snaplen truncation");
        malformed = pcap.ToArray();
        malformed[24 + 16] = 26;
        Invalid(() => new UsbPcapDecoder(1, 6).Append(malformed), "reject short USBPcap header");
        malformed = pcap.ToArray();
        malformed[24 + 16 + 23] = 6;
        Invalid(() => new UsbPcapDecoder(1, 6).Append(malformed), "reject bad USB payload length");
        malformed = pcap.ToArray();
        malformed[24 + 16 + 10] = 1;
        Invalid(() => new UsbPcapDecoder(1, 6).Append(malformed), "surface failed USB transfer");
        malformed = pcap.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(24 + 4), 1000000);
        Invalid(() => new UsbPcapDecoder(1, 6).Append(malformed), "reject invalid microsecond timestamp");
        decoder = new(1, 6);
        decoder.Append(pcap.AsSpan(0, pcap.Length - 1));
        Invalid(decoder.Complete, "detect truncated final PCAP record");
        Invalid(new UsbPcapDecoder(1, 6).Complete, "detect missing PCAP header");
        return checks;
    }

    private static byte[] PcapFixture()
    {
        var bytes = new byte[24 + 16 + 27 + 5];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xA1B2C3D4);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 65535);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 249);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 1700000000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 123456);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36), 32);
        var packet = bytes.AsSpan(40);
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 27);
        packet[16] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(packet[17..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(packet[19..], 6);
        packet[21] = 0x81;
        packet[22] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(packet[23..], 5);
        packet[31] = 1;
        return bytes;
    }

    private static void Expect(bool condition, string label)
    {
        if (!condition)
            throw new InvalidOperationException($"FAIL: {label}");
    }

    private static void Reject(Action action, string label)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new InvalidOperationException($"FAIL: {label} was accepted.");
    }
}
