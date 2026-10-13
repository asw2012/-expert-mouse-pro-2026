using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace ExpertMouse;

internal sealed class MappingForm : Form
{
    internal const string WindowTitle = AppBranding.AppName;
    private readonly DataGridView grid = new()
    {
        Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false
    };
    private readonly Label status = new() { AutoSize = true, Padding = new Padding(8), Tag = "accent" };
    private readonly Label activity = new() { AutoSize = true, Padding = new Padding(8), Tag = "muted" };
    private readonly TextBox log = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.None
    };
    private readonly Button enable = new() { Text = "Enable remapping (administrator)", AutoSize = true };
    private readonly Button save = new() { Text = "Save mappings", AutoSize = true };
    private readonly Button restart = new() { Text = "Reconnect trackball", AutoSize = true };
    private readonly CheckBox startup = new AppTheme.DarkCheckBox { Text = "Start with Windows", AutoSize = true, Enabled = false };
    private readonly NotifyIcon tray = new() { Text = AppBranding.AppName };
    private readonly ToolStripMenuItem pauseMenu = new("Pause remapping");
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    private readonly bool elevated;
    private readonly bool startInTray;
    private readonly bool previewMode;
    private readonly string logPath = Path.Combine(SettingsStore.DirectoryPath, "remapper.log");
    private AppSettings settings = Mappings.Defaults();
    private RemapperController? controller;
    private bool populating;
    private bool badSettings;
    private bool taskKnown;
    private bool taskEnabled;
    private bool ticking;
    private bool exiting;
    private bool stopped;
    private bool closing;
    private readonly PictureBox logo = new()
    {
        Size = new Size(56, 56), SizeMode = PictureBoxSizeMode.CenterImage, Margin = new Padding(0, 0, 12, 0)
    };

    internal MappingForm(bool startInTray, bool showTray = true)
    {
        this.startInTray = startInTray;
        previewMode = !showTray;
        using var identity = WindowsIdentity.GetCurrent();
        elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        Text = WindowTitle;
        Font = AppTheme.BodyFont;
        Width = 1180;
        Height = 980;
        MinimumSize = new Size(1040, 780);
        Padding = new Padding(18);
        StartPosition = FormStartPosition.CenterScreen;
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 0, 0, 12)
        };
        var brand = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        logo.Image = AppBranding.CreateLogo(48);
        brand.Controls.Add(logo);
        var heading = new TableLayoutPanel { AutoSize = true, ColumnCount = 1 };
        heading.Controls.Add(new Label
        {
            Text = AppBranding.AppName, Font = AppTheme.HeadingFont, AutoSize = true, Margin = Padding.Empty
        });
        heading.Controls.Add(new Label
        {
            Text = "TRACKBALL CONTROL CENTER", AutoSize = true, Tag = "muted", Margin = new Padding(2, 0, 0, 8)
        });
        brand.Controls.Add(heading);
        header.Controls.Add(brand);
        header.Controls.Add(new Label
        {
            AutoSize = true, Padding = new Padding(8), MaximumSize = new Size(1080, 0), Tag = "muted",
            Text = "Assign shortcuts, mouse actions, or applications to your buttons. Save, then hide to the tray to activate.\r\n" +
                "Shortcuts: Ctrl+C, Ctrl+Shift+Esc, Win+E, F13. PassThrough keeps the original action; Disabled suppresses it.\r\n" +
                "Keep X-Mouse stopped. Main-button and wheel mappings pause when another pointer is connected."
        });
        header.Controls.Add(status);
        header.Controls.Add(activity);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, Padding = new Padding(6) };
        var browse = new Button { Text = "Choose application...", AutoSize = true };
        var reset = new Button { Text = "Reset to defaults", AutoSize = true };
        var hide = new Button { Text = "Hide to tray", AutoSize = true };
        var viewLog = new Button { Text = "Open log", AutoSize = true };
        var test = new Button { Text = "Open safe test window", AutoSize = true };
        actions.Controls.AddRange([save, browse, reset, hide, enable, restart, startup, viewLog, test]);
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            Size = new Size(1100, 660), SplitterDistance = 500,
            Panel1MinSize = 220, Panel2MinSize = 64, SplitterWidth = 8
        };
        split.Panel1.Controls.Add(grid);
        split.Panel2.Controls.Add(log);
        Controls.Add(split);
        Controls.Add(actions);
        Controls.Add(header);
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Control", HeaderText = "Physical control", ReadOnly = true });
        grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "Action", HeaderText = "Action", DataSource = Enum.GetNames<MappingAction>(),
            FlatStyle = FlatStyle.Flat
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Value", HeaderText = "Shortcut / mouse button / application path", FillWeight = 170 });
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty)
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        grid.CellValueChanged += (_, e) => ChangeActionCell(e);
        grid.DataError += (_, e) =>
        {
            e.ThrowException = false;
            ShowError("Invalid mapping value. Choose one of the listed actions or mouse buttons.");
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open button mappings", null, (_, _) => ShowSettings());
        menu.Items.Add(pauseMenu);
        menu.Items.Add("Reconnect trackball", null, async (_, _) => await RestartCapture());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => RequestExit());
        AppTheme.PaintMenu(menu);
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowSettings();
        pauseMenu.Click += (_, _) =>
        {
            if (controller is null)
                return;
            controller.SetPaused(!controller.Paused);
            pauseMenu.Checked = controller.Paused;
        };
        save.Click += async (_, _) => await SaveMappings();
        browse.Click += (_, _) => BrowseApplication();
        reset.Click += (_, _) => Populate(Mappings.Defaults());
        hide.Click += (_, _) => HideToTray();
        enable.Click += (_, _) => Elevate();
        restart.Click += async (_, _) => await RestartCapture();
        viewLog.Click += (_, _) => OpenLog();
        test.Click += (_, _) => OpenTest();
        log.TextChanged += (_, _) => UpdateLogScrollBar();
        log.SizeChanged += (_, _) => UpdateLogScrollBar();
        VisibleChanged += (_, _) => controller?.SetEditing(Visible);
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
                HideToTray();
        };
        timer.Tick += async (_, _) => await TickController();
        if (!previewMode)
            Shown += async (_, _) => await Initialize();
        Shown += (_, _) =>
        {
            var desired = grid.ColumnHeadersHeight + grid.Rows.Cast<DataGridViewRow>().Sum(r => r.Height) + 4;
            split.SplitterDistance = Math.Clamp(desired, split.Panel1MinSize,
                Math.Max(split.Panel1MinSize, split.ClientSize.Height - split.Panel2MinSize - split.SplitterWidth));
        };
        FormClosing += SaveAndClose;
        AppTheme.Apply(this, save);
        tray.Icon = Icon;
        tray.Visible = showTray;
        Disposed += (_, _) =>
        {
            tray.Dispose();
            timer.Dispose();
            logo.Image?.Dispose();
            menu.Dispose();
        };
    }

    protected override bool ShowWithoutActivation => previewMode;

    private async Task Initialize()
    {
        try
        {
            settings = SettingsStore.Load();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            badSettings = true;
            ShowError($"Settings could not be loaded; remapping is OFF. Repair the assignments and Save, " +
                $"or Reset to defaults and Save.\r\n{ex.Message}");
        }
        Populate(settings);
        enable.Visible = !elevated;
        restart.Enabled = elevated;
        if (elevated && !badSettings)
            await StartController();
        else
            status.Text = badSettings ? "Settings error - remapping is OFF." : "Click Enable remapping, then approve Windows administrator access.";
        try
        {
            taskEnabled = await StartupTask.IsEnabled();
            startup.Checked = taskEnabled;
            taskKnown = true;
            startup.Enabled = elevated;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.Xml.XmlException)
        {
            ShowError($"Could not read the Windows startup task. Startup controls are disabled.\r\n{ex.Message}");
        }
        timer.Start();
        if (startInTray && elevated && !badSettings)
            HideToTray();
    }

    private async Task StartController()
    {
        try
        {
            controller = new(settings);
            controller.Notice += Log;
            controller.ButtonActivity += (control, down) =>
                activity.Text = $"Last physical button: {Mappings.Controls.Single(c => c.Id == control).Label} {(down ? "pressed" : "released")}";
            controller.SetEditing(Visible);
            await controller.Restart();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ShowError($"Remapper could not start: {ex.Message}");
        }
    }

    internal void Populate(AppSettings value)
    {
        populating = true;
        grid.Rows.Clear();
        foreach (var definition in Mappings.Controls)
        {
            var binding = value.Bindings.Single(b => b.Control == definition.Id);
            var index = grid.Rows.Add(definition.Label, binding.Action.ToString(), binding.Value);
            grid.Rows[index].Tag = definition.Id;
            SetValueCell(grid.Rows[index], binding.Action, binding.Value);
        }
        populating = false;
    }
    internal static void SetValueCell(DataGridViewRow row, MappingAction action, string value)
    {
        row.Cells[2] = action == MappingAction.MouseButton
            ? new DataGridViewComboBoxCell { DataSource = Enum.GetNames<OutputButton>(), Value = value, FlatStyle = FlatStyle.Flat }
            : new DataGridViewTextBoxCell { Value = value };
        row.Cells[2].ReadOnly = action is MappingAction.Disabled or MappingAction.PassThrough;
    }
    private void ChangeActionCell(DataGridViewCellEventArgs e)
    {
        if (populating || e.RowIndex < 0 || e.ColumnIndex != 1)
            return;
        var row = grid.Rows[e.RowIndex];
        if (!Enum.TryParse<MappingAction>(Convert.ToString(row.Cells[1].Value), out var action))
            return;
        populating = true;
        SetValueCell(row, action, action == MappingAction.MouseButton ? "Left" : "");
        populating = false;
    }

    private async Task SaveMappings()
    {
        save.Enabled = false;
        try
        {
            if (!grid.EndEdit())
                throw new InvalidOperationException("The current mapping value could not be committed.");
            var bindings = new List<Binding>();
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.Tag is not ControlId control ||
                    !Enum.TryParse<MappingAction>(Convert.ToString(row.Cells[1].Value), out var action))
                    throw new InvalidOperationException("An assignment row is invalid.");
                bindings.Add(new(control, action, Convert.ToString(row.Cells[2].Value)?.Trim() ?? ""));
            }
            var candidate = new AppSettings(1, bindings);
            SettingsStore.Save(candidate);
            settings = candidate;
            badSettings = false;
            if (controller is not null)
                controller.Apply(settings);
            else if (elevated)
                await StartController();
            Log("Mappings saved. Hide this window to the tray to activate them.");
            if (taskKnown && startup.Checked != taskEnabled)
            {
                if (startup.Checked && MessageBox.Show(this,
                    "Create a Windows logon task for this account? It starts this app with administrator access " +
                    "to the installed USBPcap driver. It does not run as SYSTEM or change Windows security settings.",
                    "Start with Windows", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                    startup.Checked = taskEnabled;
                else
                {
                    await StartupTask.SetEnabled(startup.Checked);
                    taskEnabled = startup.Checked;
                    Log(taskEnabled ? "Windows startup enabled for this account." : "Windows startup disabled.");
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or AggregateException or IOException or
                                   UnauthorizedAccessException or Win32Exception or System.Xml.XmlException)
        {
            ShowError(ex.Message);
        }
        finally
        {
            save.Enabled = true;
        }
    }
    private void BrowseApplication()
    {
        if (grid.CurrentRow is not { } row)
            return;
        using var picker = new OpenFileDialog { Filter = "Applications (*.exe)|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK)
            return;
        populating = true;
        row.Cells[1].Value = MappingAction.LaunchApplication.ToString();
        SetValueCell(row, MappingAction.LaunchApplication, picker.FileName);
        populating = false;
    }
    private void Elevate()
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot locate the app executable."))
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = Environment.CurrentDirectory
            };
            start.ArgumentList.Add("--app");
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Windows did not reopen the remapper as administrator.");
            RequestExit();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            ShowError($"Administrator request was not completed: {ex.Message}");
        }
    }
    private void HideToTray()
    {
        Hide();
        controller?.SetEditing(false);
        tray.ShowBalloonTip(2500, AppBranding.AppName, "Double-click the blue M to edit mappings.", ToolTipIcon.Info);
    }
    private void ShowSettings()
    {
        controller?.SetEditing(true);
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }
    private async Task RestartCapture()
    {
        if (controller is null || exiting)
            return;
        restart.Enabled = false;
        try { await controller.Restart(); }
        finally { restart.Enabled = elevated; }
    }
    private async Task TickController()
    {
        if (ticking || controller is null || exiting)
            return;
        ticking = true;
        try
        {
            await controller.Tick();
            status.Text = controller.State;
            tray.Text = AppBranding.AppName + " - " + (controller.Active ? "active" : "paused");
        }
        finally { ticking = false; }
    }
    private void Log(string text)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {text}{Environment.NewLine}";
        log.AppendText(line);
        if (log.TextLength > 100000)
            log.Text = log.Text[^50000..];
        try
        {
            Directory.CreateDirectory(SettingsStore.DirectoryPath);
            if (File.Exists(logPath) && new FileInfo(logPath).Length > 1024 * 1024)
                File.Move(logPath, logPath + ".previous", true);
            File.AppendAllText(logPath, line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.AppendText($"ERROR: Could not write the app log: {ex.Message}{Environment.NewLine}");
            status.Text = "App log could not be written; see the on-screen error.";
        }
        if (text.StartsWith("ERROR:", StringComparison.Ordinal))
            tray.ShowBalloonTip(4000, AppBranding.AppName + " - paused", text, ToolTipIcon.Error);
    }
    private void UpdateLogScrollBar()
    {
        if (!log.IsHandleCreated)
            return;
        var lines = log.GetLineFromCharIndex(log.TextLength) + 1;
        var desired = lines * log.Font.Height > log.ClientSize.Height ? ScrollBars.Vertical : ScrollBars.None;
        if (log.ScrollBars != desired)
            log.ScrollBars = desired;
    }
    private void ShowError(string message)
    {
        Log("ERROR: " + message);
        MessageBox.Show(this, message, AppBranding.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
    internal void ReportUnexpectedError(Exception error)
    {
        controller?.SetPaused(true);
        ShowError("An unexpected app error paused remapping.\r\n" + error);
    }
    private void OpenLog()
    {
        try
        {
            if (!File.Exists(logPath))
                throw new InvalidOperationException("No app log has been written yet.");
            using var process = Process.Start(new ProcessStartInfo("notepad.exe")
            {
                UseShellExecute = true, Arguments = "\"" + logPath + "\""
            }) ?? throw new InvalidOperationException("Windows did not open the app log.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { ShowError(ex.Message); }
    }
    private void RequestExit()
    {
        exiting = true;
        Close();
    }
    private void OpenTest()
    {
        try
        {
            var start = new ProcessStartInfo(Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot locate the test executable.")) { UseShellExecute = true };
            start.ArgumentList.Add("--input-test");
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Windows did not open the test window.");
            HideToTray();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { ShowError(ex.Message); }
    }
    private async void SaveAndClose(object? sender, FormClosingEventArgs e)
    {
        if (stopped)
            return;
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        e.Cancel = true;
        if (closing)
            return;
        closing = true;
        exiting = true;
        timer.Stop();
        if (controller is not null)
        {
            try { await controller.DisposeAsync(); }
            catch (AggregateException ex) { ShowError(ex.Message); }
        }
        stopped = true;
        tray.Visible = false;
        tray.Dispose();
        timer.Dispose();
        Close();
    }
}
