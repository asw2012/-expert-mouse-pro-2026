using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ExpertMouse;

internal static class AppBranding
{
    internal const string AppName = "Expert Mouse Pro 2026";
    internal static readonly Color Blue = Color.FromArgb(77, 158, 255);
    internal static readonly int[] IconSizes = [16, 20, 24, 32, 48, 64, 128, 256];

    internal static Bitmap CreateLogo(int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.ScaleTransform(size / 64f, size / 64f);
        using var brush = new SolidBrush(Blue);
        graphics.FillPolygon(brush,
        [
            new PointF(8, 52), new PointF(8, 12), new PointF(20, 12),
            new PointF(32, 31), new PointF(44, 12), new PointF(56, 12),
            new PointF(56, 52), new PointF(44, 52), new PointF(44, 32),
            new PointF(32, 49), new PointF(20, 32), new PointF(20, 52)
        ]);
        return bitmap;
    }

    internal static byte[] IconBytes()
    {
        var frames = new List<byte[]>();
        foreach (var size in IconSizes)
        {
            using var bitmap = CreateLogo(size);
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            frames.Add(png.ToArray());
        }
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Count);
        var offset = 6 + frames.Count * 16;
        for (var i = 0; i < frames.Count; i++)
        {
            writer.Write((byte)(IconSizes[i] == 256 ? 0 : IconSizes[i]));
            writer.Write((byte)(IconSizes[i] == 256 ? 0 : IconSizes[i]));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var frame in frames)
            writer.Write(frame);
        return stream.ToArray();
    }

    internal static Icon CreateIcon(int size = 32)
    {
        using var stream = new MemoryStream(IconBytes());
        using var icon = new Icon(stream, size, size);
        return (Icon)icon.Clone();
    }
}
