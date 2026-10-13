namespace ExpertMouse;

internal sealed class InputTestForm : Form
{
    internal InputTestForm()
    {
        Text = AppBranding.AppName + " - input test";
        Width = 800;
        Height = 560;
        KeyPreview = true;
        var instructions = new Label
        {
            AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10),
            Text = "Only this focused window displays received input. Nothing is typed into other apps by this test window.\r\n" +
                "Press a mapped button. Shortcuts show key events; mapped mouse buttons show DOWN and UP.\r\n" +
                "For a drag, hold a mapped button, move the ball, then release. Close this window when finished."
        };
        var events = new ListBox { Dock = DockStyle.Fill };
        var recorded = new List<string>();
        var save = new Button { Text = "Save test events and close", Dock = DockStyle.Bottom, Height = 38 };
        var lastMove = DateTime.UtcNow;
        void Record(string text)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} {text}";
            recorded.Add(line);
            events.Items.Add(line);
            if (events.Items.Count > 300) events.Items.RemoveAt(0);
            if (recorded.Count > 2000) recorded.RemoveAt(0);
            events.TopIndex = events.Items.Count - 1;
        }
        events.MouseDown += (_, e) => Record($"Mouse DOWN {e.Button}");
        events.MouseUp += (_, e) => Record($"Mouse UP {e.Button}");
        events.MouseWheel += (_, e) => Record($"Wheel {e.Delta}");
        events.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.None && DateTime.UtcNow - lastMove > TimeSpan.FromMilliseconds(100))
            {
                lastMove = DateTime.UtcNow;
                Record($"Mouse MOVE held={e.Button}");
            }
        };
        KeyDown += (_, e) => { Record($"Key DOWN {e.KeyCode} modifiers={e.Modifiers}"); e.SuppressKeyPress = true; };
        KeyUp += (_, e) => Record($"Key UP {e.KeyCode} modifiers={e.Modifiers}");
        Controls.Add(events);
        Controls.Add(save);
        Controls.Add(instructions);
        save.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(SettingsStore.DirectoryPath);
                var path = Path.Combine(SettingsStore.DirectoryPath, "input-test-events.json");
                File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(recorded, Mappings.Json));
                Close();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, ex.Message, "Test events not saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        };
        Shown += (_, _) => events.Focus();
        AppTheme.Apply(this, save);
    }
}
