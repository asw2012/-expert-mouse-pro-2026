using System.Buffers.Binary;

namespace ExpertMouse;

internal sealed record UsbReport(DateTimeOffset Time, ushort Bus, ushort Address,
    byte Endpoint, string Hex);

internal sealed class UsbPcapDecoder(ushort bus, ushort address)
{
    private readonly byte[] buffer = new byte[2 * 1024 * 1024];
    private int used;
    private bool headerRead;
    internal long Records { get; private set; }
    internal bool HeaderRead => headerRead;

    internal List<UsbReport> Append(ReadOnlySpan<byte> chunk)
    {
        if (chunk.Length > buffer.Length - used)
            throw new InvalidOperationException("USB capture exceeded the parser buffer limit.");
        chunk.CopyTo(buffer.AsSpan(used));
        used += chunk.Length;
        var offset = 0;
        var reports = new List<UsbReport>();
        if (!headerRead)
        {
            if (used < 24)
                return reports;
            var header = buffer.AsSpan(0, 24);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xA1B2C3D4 ||
                BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != 2 ||
                BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != 4 ||
                BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) != 249)
                throw new InvalidOperationException("Expected little-endian PCAP 2.4 with USBPcap link type 249.");
            headerRead = true;
            offset = 24;
        }
        while (used - offset >= 16)
        {
            var header = buffer.AsSpan(offset, 16);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
            var original = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
            if (length < 27 || length > 1024 * 1024 || original != length)
                throw new InvalidOperationException("Invalid or truncated USBPcap record; capture must not truncate reports.");
            if ((uint)(used - offset - 16) < length)
                break;
            var packet = buffer.AsSpan(offset + 16, checked((int)length));
            var headerLength = BinaryPrimitives.ReadUInt16LittleEndian(packet);
            var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(packet[23..]);
            if (headerLength < 27 || headerLength > length || dataLength != length - headerLength)
                throw new InvalidOperationException("USBPcap header and payload lengths disagree.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(packet[17..]) != bus ||
                BinaryPrimitives.ReadUInt16LittleEndian(packet[19..]) != address)
                throw new InvalidOperationException("Capture contains a different USB device; refusing to record it.");
            Records++;
            if (packet[22] == 1 && (packet[16] & 1) != 0 && (packet[21] & 0x80) != 0)
            {
                var status = BinaryPrimitives.ReadUInt32LittleEndian(packet[10..]);
                if (status != 0)
                    throw new InvalidOperationException($"Trackball interrupt transfer failed: USB status 0x{status:X8}.");
                if (dataLength != 0)
                {
                    var seconds = BinaryPrimitives.ReadUInt32LittleEndian(header);
                    var micros = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
                    if (micros >= 1000000)
                        throw new InvalidOperationException("Invalid PCAP microsecond timestamp.");
                    reports.Add(new(DateTimeOffset.FromUnixTimeSeconds(seconds).AddTicks(micros * 10L),
                        bus, address, packet[21], Convert.ToHexString(packet[headerLength..])));
                }
            }
            offset += 16 + checked((int)length);
        }
        buffer.AsSpan(offset, used - offset).CopyTo(buffer);
        used -= offset;
        return reports;
    }

    internal void Complete()
    {
        if (!headerRead || used != 0)
            throw new InvalidOperationException("USB capture ended with a missing header or partial record.");
    }
}
