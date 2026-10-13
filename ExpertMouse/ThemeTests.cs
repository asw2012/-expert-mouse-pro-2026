namespace ExpertMouse;

internal static class ThemeTests
{
    internal static int Run()
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            count++;
            if (!condition) throw new InvalidOperationException("FAIL: " + label);
        }
        Check(AppBranding.AppName == "Expert Mouse Pro 2026", "2026 app display name");
        using var small = AppBranding.CreateLogo(16);
        var blue = 0;
        for (var y = 0; y < small.Height; y++)
            for (var x = 0; x < small.Width; x++)
            {
                var pixel = small.GetPixel(x, y);
                if (pixel.A > 128 && pixel.B > pixel.G && pixel.G > pixel.R)
                    blue++;
            }
        Check(blue >= 50, "blue M stays visible at 16-pixel tray size");
        Check(small.GetPixel(0, 0).A == 0, "tray icon has a transparent background");
        var bytes = AppBranding.IconBytes();
        Check(BitConverter.ToUInt16(bytes, 2) == 1 &&
            BitConverter.ToUInt16(bytes, 4) == AppBranding.IconSizes.Length, "ICO contains every requested DPI size");
        for (var i = 0; i < AppBranding.IconSizes.Length; i++)
        {
            var entry = 6 + i * 16;
            var length = BitConverter.ToInt32(bytes, entry + 8);
            var offset = BitConverter.ToInt32(bytes, entry + 12);
            Check(offset >= 6 + AppBranding.IconSizes.Length * 16 && length > 0 && offset + length <= bytes.Length,
                "ICO frame data is in bounds");
        }
        using var icon = AppBranding.CreateIcon(32);
        Check(icon.Size == new Size(32, 32), "Windows can load the blue M icon");
        Check(Contrast(AppTheme.Text, AppTheme.Surface) >= 7, "primary text contrast at least 7:1");
        Check(Contrast(AppTheme.Muted, AppTheme.Background) >= 4.5, "secondary text contrast at least 4.5:1");
        Check(Contrast(AppTheme.Accent, AppTheme.Background) >= 4.5, "accent text contrast at least 4.5:1");
        Check(AppTheme.BodyFont.Name.StartsWith("Segoe UI", StringComparison.Ordinal), "modern Segoe UI font resolves");
        using var fallback = AppTheme.CreateFont("Unavailable Expert Mouse Font", "Segoe UI", 10f);
        Check(fallback.Name.StartsWith("Segoe UI", StringComparison.Ordinal), "standard Segoe UI fallback resolves on build runners");
        using (var form = new MappingForm(false, showTray: false))
        {
            form.Populate(Mappings.Defaults());
            Check(form.Text == AppBranding.AppName && form.Icon is not null, "settings window uses updated name/icon");
            if (!SystemInformation.HighContrast)
                Check(form.BackColor == AppTheme.Background && form.ForeColor == AppTheme.Text, "dark settings palette applied");
            var grid = Descendants(form).OfType<DataGridView>().Single();
            Check(grid.Rows.Count == 13, "all mapping controls survive visual refresh");
            if (!SystemInformation.HighContrast)
                Check(grid.DefaultCellStyle.BackColor == AppTheme.Surface &&
                    grid.DefaultCellStyle.SelectionForeColor == AppTheme.Text, "readable grid and selection colors");
        }
        return count;
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static double Contrast(Color a, Color b)
    {
        static double Luminance(Color color)
        {
            static double Channel(byte value)
            {
                var s = value / 255d;
                return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return Channel(color.R) * 0.2126 + Channel(color.G) * 0.7152 + Channel(color.B) * 0.0722;
        }
        var first = Luminance(a);
        var second = Luminance(b);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }
}
