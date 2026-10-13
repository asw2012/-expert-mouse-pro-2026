using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ExpertMouse;

internal static class AppTheme
{
    internal static readonly Color Background = Color.FromArgb(15, 20, 29);
    internal static readonly Color Surface = Color.FromArgb(24, 32, 45);
    internal static readonly Color Raised = Color.FromArgb(31, 42, 58);
    internal static readonly Color Border = Color.FromArgb(45, 59, 80);
    internal static readonly Color Text = Color.FromArgb(229, 237, 249);
    internal static readonly Color Muted = Color.FromArgb(154, 172, 196);
    internal static readonly Color Selection = Color.FromArgb(30, 65, 108);
    internal static readonly Color Accent = AppBranding.Blue;
    internal static readonly Font BodyFont = CreateFont("Segoe UI Variable Text", "Segoe UI", 10f);
    internal static readonly Font ButtonFont = new("Segoe UI", 9.5f, FontStyle.Bold);
    internal static readonly Font HeadingFont = CreateFont("Segoe UI Variable Display", "Segoe UI", 21f, FontStyle.Bold);
    internal static readonly Font LogFont = new("Consolas", 9f);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, uint size);

    internal static Font CreateFont(string preferred, string fallback, float size, FontStyle style = FontStyle.Regular)
    {
        var font = new Font(preferred, size, style);
        if (font.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase))
            return font;
        font.Dispose();
        return new Font(fallback, size, style);
    }

    internal static void Apply(Form form, Button? primary = null)
    {
        form.Font = BodyFont;
        var icon = AppBranding.CreateIcon(64);
        form.Icon = icon;
        form.Disposed += (_, _) => icon.Dispose();
        if (SystemInformation.HighContrast)
            return;
        PaintControl(form);
        if (primary is not null)
        {
            primary.BackColor = Accent;
            primary.ForeColor = Background;
            primary.FlatAppearance.BorderColor = Accent;
            primary.FlatAppearance.MouseOverBackColor = Color.FromArgb(110, 179, 255);
            primary.FlatAppearance.MouseDownBackColor = Color.FromArgb(63, 135, 224);
        }
        form.HandleCreated += (_, _) =>
        {
            var enabled = 1;
            var result = DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int));
            if (result < 0)
                throw new Win32Exception($"Could not apply the dark window title bar (0x{result:X8}).");
        };
    }

    private static void PaintControl(Control control)
    {
        control.BackColor = control is TextBoxBase or ListBox ? Surface : Background;
        control.ForeColor = Text;
        switch (control)
        {
            case Button button:
                button.FlatStyle = FlatStyle.Flat;
                button.UseVisualStyleBackColor = false;
                button.BackColor = Raised;
                button.Font = ButtonFont;
                button.Padding = new Padding(12, 7, 12, 7);
                button.Margin = new Padding(4);
                button.FlatAppearance.BorderSize = 1;
                button.FlatAppearance.BorderColor = Border;
                button.FlatAppearance.MouseOverBackColor = Selection;
                button.FlatAppearance.MouseDownBackColor = Surface;
                break;
            case CheckBox check:
                check.FlatStyle = FlatStyle.Flat;
                check.Padding = new Padding(6);
                check.Margin = new Padding(6);
                break;
            case TextBoxBase text:
                text.BorderStyle = BorderStyle.FixedSingle;
                if (text.Multiline)
                    text.Font = LogFont;
                break;
            case ListBox list:
                list.BorderStyle = BorderStyle.None;
                list.Font = LogFont;
                break;
            case ComboBox combo:
                PaintCombo(combo);
                break;
            case DataGridView grid:
                PaintGrid(grid);
                return;
            case Label label:
                if (label.Tag is "muted")
                    label.ForeColor = Muted;
                if (label.Tag is "accent")
                    label.ForeColor = Accent;
                break;
        }
        foreach (Control child in control.Controls)
            PaintControl(child);
    }

    internal static void PaintMenu(ContextMenuStrip menu)
    {
        if (SystemInformation.HighContrast)
            return;
        menu.Font = BodyFont;
        menu.BackColor = Surface;
        menu.ForeColor = Text;
        menu.Renderer = new ToolStripProfessionalRenderer(new MenuColors());
        foreach (ToolStripItem item in menu.Items)
            item.ForeColor = Text;
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Selection;
        public override Color MenuItemSelected => Selection;
        public override Color MenuItemSelectedGradientBegin => Selection;
        public override Color MenuItemSelectedGradientEnd => Selection;
        public override Color MenuItemPressedGradientBegin => Raised;
        public override Color MenuItemPressedGradientMiddle => Raised;
        public override Color MenuItemPressedGradientEnd => Raised;
        public override Color CheckBackground => Selection;
        public override Color CheckSelectedBackground => Selection;
        public override Color CheckPressedBackground => Selection;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }

    internal static void PaintGrid(DataGridView grid)
    {
        grid.EnableHeadersVisualStyles = false;
        grid.BackgroundColor = Surface;
        grid.GridColor = Border;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 42;
        grid.ColumnHeadersDefaultCellStyle = new()
        {
            BackColor = Raised, ForeColor = Muted, Font = ButtonFont,
            SelectionBackColor = Raised, SelectionForeColor = Text, Padding = new Padding(10, 0, 10, 0)
        };
        grid.DefaultCellStyle = new()
        {
            BackColor = Surface, ForeColor = Text, Font = BodyFont,
            SelectionBackColor = Selection, SelectionForeColor = Text, Padding = new Padding(10, 4, 10, 4)
        };
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(20, 28, 40);
        grid.RowTemplate.Height = 32;
        grid.CellPainting -= PaintComboCell;
        grid.CellPainting += PaintComboCell;
        grid.EditingControlShowing += (_, e) =>
        {
            e.Control.BackColor = Raised;
            e.Control.ForeColor = Text;
            e.Control.Font = BodyFont;
            if (e.Control is ComboBox combo)
                PaintCombo(combo);
        };
    }

    private static void PaintComboCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (sender is not DataGridView grid || e.RowIndex < 0 || e.ColumnIndex < 0 ||
            grid.Rows[e.RowIndex].Cells[e.ColumnIndex] is not DataGridViewComboBoxCell || e.Graphics is null)
            return;
        e.Paint(e.ClipBounds, DataGridViewPaintParts.Background | DataGridViewPaintParts.SelectionBackground |
            DataGridViewPaintParts.Border | DataGridViewPaintParts.Focus);
        var button = new Rectangle(e.CellBounds.Right - 27, e.CellBounds.Top + 6, 20, e.CellBounds.Height - 12);
        using var fill = new SolidBrush(Raised);
        e.Graphics.FillRectangle(fill, button);
        using var arrow = new SolidBrush(Muted);
        var center = button.Left + button.Width / 2;
        var y = button.Top + button.Height / 2;
        e.Graphics.FillPolygon(arrow, [new Point(center - 4, y - 2), new Point(center + 4, y - 2), new Point(center, y + 2)]);
        var text = new Rectangle(e.CellBounds.Left + 12, e.CellBounds.Top, e.CellBounds.Width - 44, e.CellBounds.Height);
        TextRenderer.DrawText(e.Graphics, Convert.ToString(e.FormattedValue), BodyFont, text, Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.Handled = true;
    }

    private static void PaintCombo(ComboBox combo)
    {
        combo.FlatStyle = FlatStyle.Flat;
        combo.BackColor = Raised;
        combo.ForeColor = Text;
        combo.DrawMode = DrawMode.OwnerDrawFixed;
        combo.ItemHeight = 25;
        combo.DrawItem -= DrawComboItem;
        combo.DrawItem += DrawComboItem;
    }

    internal sealed class DarkCheckBox : CheckBox
    {
        internal DarkCheckBox() =>
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        protected override void OnPaint(PaintEventArgs e)
        {
            if (SystemInformation.HighContrast)
            {
                base.OnPaint(e);
                return;
            }
            e.Graphics.Clear(BackColor);
            var box = new Rectangle(Padding.Left + 2, (Height - 16) / 2, 16, 16);
            using var fill = new SolidBrush(Checked ? AppTheme.Accent : AppTheme.Surface);
            e.Graphics.FillRectangle(fill, box);
            using var border = new Pen(Checked ? AppTheme.Accent : AppTheme.Muted);
            e.Graphics.DrawRectangle(border, box);
            if (Checked)
            {
                using var check = new Pen(AppTheme.Background, 2);
                e.Graphics.DrawLines(check,
                    [new Point(box.Left + 3, box.Top + 8), new Point(box.Left + 7, box.Top + 12), new Point(box.Left + 13, box.Top + 4)]);
            }
            var text = new Rectangle(box.Right + 8, 0, Width - box.Right - 8, Height);
            TextRenderer.DrawText(e.Graphics, Text, Font, text, Enabled ? AppTheme.Text : AppTheme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle, AppTheme.Text, BackColor);
        }
    }
    private static void DrawComboItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not ComboBox combo)
            return;
        using var brush = new SolidBrush((e.State & DrawItemState.Selected) != 0 ? Selection : Raised);
        e.Graphics.FillRectangle(brush, e.Bounds);
        var text = e.Index >= 0 ? combo.GetItemText(combo.Items[e.Index]) : combo.Text;
        TextRenderer.DrawText(e.Graphics, text, BodyFont, e.Bounds, Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        e.DrawFocusRectangle();
    }
}
