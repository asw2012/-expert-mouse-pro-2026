using System.Buffers.Binary;

namespace ExpertMouse;

internal sealed record RawPacket(
    DateTimeOffset Time, string Kind, ushort MotionFlags, ushort ButtonFlags,
    short ButtonData, uint RawButtons, int X, int Y, uint ExtraInformation, string? HidHex)
{
    internal static RawPacket Decode(uint type, ReadOnlySpan<byte> data)
    {
        var now = DateTimeOffset.UtcNow;
        if (type == 0)
        {
            if (data.Length < 24)
                throw new InvalidOperationException("Truncated RAWMOUSE packet.");
            return new(now, "Mouse", BinaryPrimitives.ReadUInt16LittleEndian(data),
                BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),
                BinaryPrimitives.ReadInt16LittleEndian(data[6..]),
                BinaryPrimitives.ReadUInt32LittleEndian(data[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(data[12..]),
                BinaryPrimitives.ReadInt32LittleEndian(data[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(data[20..]), null);
        }
        if (type == 2)
        {
            if (data.Length < 8)
                throw new InvalidOperationException("Truncated RAWHID header.");
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data);
            var count = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            var length = (ulong)size * count;
            if (length > (ulong)(data.Length - 8) || length == 0)
                throw new InvalidOperationException("Invalid RAWHID report lengths.");
            return new(now, "Hid", 0, 0, 0, 0, 0, 0, 0,
                Convert.ToHexString(data.Slice(8, checked((int)length))));
        }
        throw new InvalidOperationException($"Unexpected raw input type {type} for the Expert Mouse.");
    }
}
