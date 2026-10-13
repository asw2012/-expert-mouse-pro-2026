using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace ExpertMouse;

internal sealed record UsbButtonTest(string Label, bool Completed, DateTimeOffset Started,
    DateTimeOffset Finished, IReadOnlyList<UsbReport> Reports);

internal sealed record UsbCaptureResult(int FormatVersion, UsbTarget? Target, string PcapPath,
    long ReportCount, IReadOnlyList<UsbButtonTest> Tests, IReadOnlyList<string> Errors);

internal sealed class UsbCaptureForm : Form
{
    private readonly string output;
    private readonly string pcapPath;
    private readonly List<UsbButtonTest> tests = [];
    private readonly List<string> errors = [];
    private readonly List<UsbReport> reports = [];
    private readonly ComboBox label = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
    private readonly Button start = new() { Text = "Start selected test", AutoSize = true, Enabled = false };
    private readonly Button save = new() { Text = "Save results and close", AutoSize = true };
    private readonly Button enable = new() { Text = "Enable USB capture (administrator)", AutoSize = true };
    private readonly Label status = new() { AutoSize = true, Padding = new Padding(6) };
    private readonly TextBox log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        WordWrap = false, Dock = DockStyle.Fill
    };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private UsbCaptureSession? session;
    private UsbTarget? target;
    private string? active;
    private DateTimeOffset started;
    private DateTimeOffset nextHealthCheck;
    private bool failed;
    private bool closing;
    private bool stopped;
    private bool ownsOutput;
    private string? lastHex;

    internal UsbCaptureForm(string output)
    {
        this.output = output;
        pcapPath = Path.ChangeExtension(output, ".pcap");
        Text = AppBranding.AppName + " - USB capture";
        Width = 1040;
        Height = 720;
        MinimumSize = new Size(850, 550);
        StartPosition = FormStartPosition.CenterScreen;
        var header = new TableLayoutPanel
        {
            AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Padding = new Padding(8)
        };
        header.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(950, 0),
            Text = "RAW USB capture: four main buttons, middle/wheel click, wheel scrolling, and six DirectLaunch buttons.\r\n" +
                "Select a test and click Start. WAIT two seconds, then press/hold for one second and release.\r\n" +
                "For scrolling, turn the wheel only in the selected direction. Each test ends automatically after five seconds.\r\n" +
                "Keep the ball still and do not click window controls during TEST NOW. Normal mouse actions are not blocked."
        });
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        label.Items.AddRange(CaptureForm.TestLabels);
        label.SelectedIndex = 0;
        start.Click += (_, _) => StartTest();
        save.Click += (_, _) => Close();
        enable.Click += (_, _) => EnableCapture();
        buttons.Controls.AddRange([label, start, save, enable]);
        header.Controls.Add(buttons);
        header.Controls.Add(status);
        Controls.Add(log);
        Controls.Add(header);
        log.AppendText($"JSON: {output}\r\nRaw trace: {pcapPath}\r\n");
        Shown += (_, _) => StartCapture();
        timer.Tick += (_, _) => TickCapture();
        FormClosing += SaveOnClose;
        AppTheme.Apply(this, start);
    }

    private void StartCapture()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            status.Text = "Click Enable USB capture (administrator), then approve the Windows prompt.";
            log.AppendText("Administrator access is required by the installed USBPcap driver. " +
                "Click Enable USB capture in this visible window to request access.\r\n");
            save.Text = "Close";
            return;
        }
        enable.Visible = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            if (File.Exists(output) || File.Exists(pcapPath))
                throw new InvalidOperationException("Capture filenames already exist. Use a new results filename.");
            using (File.Open(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                ownsOutput = true;
            status.Text = "Discovering the trackball by USB VID/PID...";
            var targets = UsbCapture.FindTargets();
            if (targets.Count != 1)
                throw new InvalidOperationException($"Found {targets.Count} Expert Mouse Pro devices. " +
                    "Exactly one connected 047D:1002 trackball is required.");
            target = targets[0];
            session = new(target, pcapPath);
            log.AppendText($"Verified 047D:1002 on {target.CaptureDevice}, address {target.Address}. " +
                "Only that address is enabled; all-devices and new-device capture are OFF.\r\n");
            nextHealthCheck = DateTimeOffset.UtcNow.AddSeconds(1);
            timer.Start();
            WriteResults();
            UpdateStatus();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Fail(ex.Message);
        }
    }

    private void EnableCapture()
    {
        try
        {
            var launch = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath
                    ?? throw new InvalidOperationException("Cannot locate the capture executable."),
                UseShellExecute = true, Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory
            };
            launch.ArgumentList.Add("--usb-capture");
            launch.ArgumentList.Add(output);
            using var process = Process.Start(launch)
                ?? throw new InvalidOperationException("Windows did not launch the administrator capture window.");
            Close();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            log.AppendText($"Capture has not started: {ex.Message}\r\n");
            status.Text = "Administrator request was not completed. Click Enable USB capture to try again.";
            MessageBox.Show(this, ex.Message, "USB capture requires administrator approval",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void StartTest()
    {
        if (session is null || !session.Ready || failed)
            return;
        active = label.SelectedItem as string
            ?? throw new InvalidOperationException("Choose a button test.");
        reports.Clear();
        lastHex = null;
        started = DateTimeOffset.UtcNow.AddSeconds(2);
        label.Enabled = start.Enabled = save.Enabled = false;
        log.AppendText($"\r\n{active}: WAIT two seconds, then exercise ONLY this control and release it.\r\n");
        UpdateStatus();
    }

    private void TickCapture()
    {
        if (session is null || closing)
            return;
        try
        {
            DrainReports();
            var now = DateTimeOffset.UtcNow;
            if (!failed && now >= nextHealthCheck)
            {
                session.CheckHealth();
                nextHealthCheck = now.AddSeconds(1);
            }
            if (active is not null && now >= started.AddSeconds(5))
                FinishTest(true);
            UpdateStatus();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Fail(ex.Message);
        }
    }

    private void DrainReports()
    {
        if (session is null)
            return;
        while (session.TryTake(out var report))
        {
            if (report is null || active is null || report.Time < started || report.Time >= started.AddSeconds(5))
                continue;
            if (reports.Count >= 10000)
                throw new InvalidOperationException("Test exceeded 10,000 USB reports. Repeat a shorter test.");
            reports.Add(report);
            if (report.Hex != lastHex)
            {
                log.AppendText($"  {report.Time:HH:mm:ss.fff} endpoint 0x{report.Endpoint:X2}: {report.Hex}\r\n");
                lastHex = report.Hex;
            }
        }
    }

    private void FinishTest(bool completed)
    {
        if (active is null)
            return;
        var now = DateTimeOffset.UtcNow;
        tests.Add(new(active, completed, now < started ? now : started,
            completed ? started.AddSeconds(5) : now, reports.ToArray()));
        log.AppendText($"{(completed ? "Finished" : "Incomplete")} {active}: {reports.Count} raw reports; " +
            $"distinct payloads: {string.Join(", ", reports.Select(r => r.Hex).Distinct())}\r\n");
        active = null;
        label.Enabled = save.Enabled = true;
        start.Enabled = !failed;
        if (completed && label.SelectedIndex < label.Items.Count - 1)
            label.SelectedIndex++;
        WriteResults();
    }

    private void UpdateStatus()
    {
        var now = DateTimeOffset.UtcNow;
        var phase = failed ? "CAPTURE ERROR - see log" : session?.Ready != true ? "Waiting for USB capture header..." :
            active is null ? "Ready - select a button and start its test" : now < started
                ? $"WAIT {(started - now).TotalSeconds:F1}s"
                : $"TEST NOW: {active}, {(started.AddSeconds(5) - now).TotalSeconds:F1}s left";
        status.Text = $"{phase} | USB reports: {session?.ReportCount ?? 0} | " +
            $"Saved tests: {tests.Count(t => t.Completed)} | Errors: {errors.Count}";
        Text = $"{AppBranding.AppName} - USB capture - {phase}";
        start.Enabled = !failed && session?.Ready == true && active is null;
    }

    private void Fail(string message)
    {
        failed = true;
        errors.Add(message);
        log.AppendText($"ERROR: {message}\r\n");
        Console.Error.WriteLine(message);
        timer.Stop();
        if (active is not null)
        {
            // Keep error reporting usable even when saving the interrupted test fails.
            var now = DateTimeOffset.UtcNow;
            tests.Add(new(active, false, now < started ? now : started, now, reports.ToArray()));
            active = null;
        }
        label.Enabled = save.Enabled = true;
        UpdateStatus();
        if (session is not null && !closing)
            _ = StopAfterError();
    }

    private async Task StopAfterError()
    {
        try
        {
            await session!.DisposeAsync();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            errors.Add(ex.Message);
            log.AppendText($"ERROR while stopping capture: {ex.Message}\r\n");
            Console.Error.WriteLine(ex.Message);
        }
        UpdateStatus();
    }

    private void WriteResults()
    {
        var temporary = output + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(
            new UsbCaptureResult(1, target, pcapPath, session?.ReportCount ?? 0, tests, errors),
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, output, true);
    }

    private async void SaveOnClose(object? sender, FormClosingEventArgs e)
    {
        if (stopped)
            return;
        e.Cancel = true;
        if (closing)
            return;
        closing = true;
        timer.Stop();
        start.Enabled = save.Enabled = false;
        status.Text = "Stopping USB capture and saving results...";
        try
        {
            if (session is not null)
            {
                try
                {
                    await session.DisposeAsync();
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
                {
                    Fail(ex.Message);
                }
                DrainReports();
            }
            if (active is not null)
            {
                errors.Add("Window closed before the timed test finished.");
                FinishTest(false);
            }
            if (ownsOutput)
                WriteResults();
            stopped = true;
            timer.Dispose();
            Console.WriteLine(ownsOutput
                ? $"Saved {tests.Count} USB tests to {output}"
                : "USB capture was not started; no results were written.");
            Close();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Fail($"Could not save results: {ex.Message}");
            MessageBox.Show(this, ex.Message, "USB results not saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
            closing = false;
            save.Enabled = true;
        }
    }
}
