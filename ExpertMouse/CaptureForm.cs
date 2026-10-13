using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ExpertMouse;

internal sealed record ButtonTest(
    string Label, bool Completed, DateTimeOffset Started, DateTimeOffset Finished,
    IReadOnlyList<RawPacket> Packets);

internal sealed record CaptureResult(
    int FormatVersion, IReadOnlyList<DeviceDescription> Devices,
    IReadOnlyList<ButtonTest> Tests, IReadOnlyList<string> Errors);

internal sealed class CaptureForm : Form
{
    internal static readonly string[] TestLabels =
    [
        "Main button - upper left", "Main button - upper right",
        "Main button - lower left", "Main button - lower right",
        "Scroll wheel click",
        "Scroll wheel up", "Scroll wheel down",
        "DirectLaunch 1", "DirectLaunch 2", "DirectLaunch 3",
        "DirectLaunch 4", "DirectLaunch 5", "DirectLaunch 6"
    ];

    private readonly List<ConnectedDevice> devices;
    private readonly HashSet<nint> handles;
    private readonly string output;
    private readonly List<ButtonTest> tests = [];
    private readonly List<string> errors = [];
    private readonly List<RawPacket> packets = [];
    private readonly ComboBox label = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
    private readonly Button start = new() { Text = "Start selected test", AutoSize = true };
    private readonly Button save = new() { Text = "Save results and close", AutoSize = true };
    private readonly TextBox log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both,
        WordWrap = false, Dock = DockStyle.Fill
    };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 200 };
    private readonly Label status = new() { AutoSize = true };
    private string? active;
    private DateTimeOffset started;
    private bool registered;

    internal CaptureForm(List<ConnectedDevice> devices, string output)
    {
        this.devices = devices;
        this.output = output;
        handles = devices.Select(d => d.Handle).ToHashSet();
        Text = AppBranding.AppName + " - button visibility test (read-only)";
        Width = 920;
        Height = 650;
        StartPosition = FormStartPosition.CenterScreen;
        var instructions = new Label
        {
            Text = "Select a button and click Start. After the two-second countdown, press and release " +
                "that physical button during the five-second test.\r\nThe test ends automatically; do not " +
                "click any on-screen controls during it. Keep the ball still.\r\nTest all six DirectLaunch " +
                "buttons and a main button as a baseline. Mouse actions and X-Mouse are not changed.",
            AutoSize = true, Padding = new Padding(8)
        };
        var controls = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48 };
        label.Items.AddRange(TestLabels);
        label.SelectedIndex = 0;
        start.Click += (_, _) => StartTest();
        save.Click += (_, _) => Close();
        controls.Controls.AddRange([label, start, save]);
        var header = new Panel { Dock = DockStyle.Top, Height = 200 };
        instructions.Dock = DockStyle.Top;
        status.Dock = DockStyle.Bottom;
        header.Controls.Add(controls);
        header.Controls.Add(instructions);
        header.Controls.Add(status);
        controls.BringToFront();
        controls.Top = 125;
        controls.Dock = DockStyle.None;
        controls.Width = 880;
        Controls.Add(log);
        Controls.Add(header);
        log.AppendText($"Device: 047D:1002. Reports will be saved to {output}\r\n");
        timer.Tick += (_, _) => UpdateStatus();
        Shown += (_, _) => StartCapture();
        FormClosing += (_, e) => SaveOnClose(e);
        AppTheme.Apply(this, start);
    }

    private void StartCapture()
    {
        try
        {
            var registrations = devices.Select(d =>
                (d.Description.UsagePage, d.Description.Usage)).Distinct()
                .Select(u => new Native.Registration
                {
                    UsagePage = u.UsagePage, Usage = u.Usage,
                    Flags = 0x100 | 0x2000, Target = Handle
                }).ToArray();
            if (!Native.RegisterRawInputDevices(registrations, (uint)registrations.Length,
                    (uint)Marshal.SizeOf<Native.Registration>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Register Expert Mouse Raw Input");
            registered = true;
            timer.Start();
            UpdateStatus();
        }
        catch (Win32Exception ex)
        {
            RecordError(ex.Message);
            start.Enabled = false;
        }
    }

    private void StartTest()
    {
        active = label.SelectedItem as string
            ?? throw new InvalidOperationException("Choose a physical button first.");
        packets.Clear();
        started = DateTimeOffset.UtcNow.AddSeconds(2);
        label.Enabled = false;
        start.Enabled = false;
        save.Enabled = false;
        log.AppendText($"\r\nArming {active}: wait two seconds, then press and release it. " +
            "The test will end automatically five seconds later.\r\n");
        UpdateStatus();
    }

    private void FinishTest(bool completed = true)
    {
        if (active is null)
            return;
        var ended = DateTimeOffset.UtcNow;
        tests.Add(new(active, completed, ended < started ? ended : started, ended, packets.ToArray()));
        log.AppendText($"Finished {active}: {packets.Count} target packets, " +
            $"{packets.Count(p => p.ButtonFlags != 0)} button/wheel transitions, " +
            $"{packets.Count(p => p.RawButtons != 0)} nonzero raw button states.\r\n");
        active = null;
        label.Enabled = true;
        start.Enabled = registered;
        save.Enabled = true;
        if (label.SelectedIndex < label.Items.Count - 1)
            label.SelectedIndex++;
        UpdateStatus();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x00FF)
        {
            try
            {
                ReadPacket(message.LParam);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or OverflowException)
            {
                RecordError(ex.Message);
                FinishTest(false);
                start.Enabled = false;
                registered = false;
            }
        }
        if (message.Msg == 0x00FE && IsTargetRemoval(message.WParam, message.LParam, handles))
        {
            RecordError("The Expert Mouse was disconnected. Reconnect it and restart the test.");
            FinishTest(false);
            start.Enabled = false;
            registered = false;
        }
        // DefWindowProc must receive foreground WM_INPUT for native resource cleanup.
        base.WndProc(ref message);
    }

    internal static bool IsTargetRemoval(nint change, nint device, IReadOnlySet<nint> targets) =>
        change == 2 && targets.Contains(device);

    private void ReadPacket(nint rawInput)
    {
        var headerSize = (uint)Marshal.SizeOf<Native.Header>();
        uint size = 0;
        DeviceSurvey.Check(Native.GetRawInputData(rawInput, Native.Input, 0, ref size, headerSize),
            "Size Raw Input packet");
        if (size < headerSize || size > 1024 * 1024)
            throw new InvalidOperationException($"Invalid Raw Input packet size {size}.");
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            var read = Native.GetRawInputData(rawInput, Native.Input, buffer, ref size, headerSize);
            DeviceSurvey.Check(read, "Read Raw Input packet");
            if (read < headerSize)
                throw new InvalidOperationException("Truncated Raw Input header.");
            var header = Marshal.PtrToStructure<Native.Header>(buffer);
            var now = DateTimeOffset.UtcNow;
            if (!handles.Contains(header.Device) || active is null || now < started)
                return;
            if (now >= started.AddSeconds(5))
            {
                FinishTest();
                return;
            }
            if (header.Size != read)
                throw new InvalidOperationException("Raw Input header and buffer sizes disagree.");
            var bytes = new byte[read - headerSize];
            Marshal.Copy(buffer + (int)headerSize, bytes, 0, bytes.Length);
            var packet = RawPacket.Decode(header.Type, bytes);
            if (packets.Count >= 10000)
                throw new InvalidOperationException("Test exceeded 10,000 packets. Finish sooner and repeat it.");
            packets.Add(packet);
            if (packet.ButtonFlags != 0 || packet.RawButtons != 0 || packet.HidHex is not null)
                log.AppendText($"  flags=0x{packet.ButtonFlags:X4} raw=0x{packet.RawButtons:X8} " +
                    $"data={packet.ButtonData} xy=({packet.X},{packet.Y}) HID={packet.HidHex ?? "-"}\r\n");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void UpdateStatus()
    {
        var now = DateTimeOffset.UtcNow;
        if (active is not null && now >= started.AddSeconds(5))
        {
            FinishTest();
            return;
        }
        var phase = active is null ? "Ready" : now < started
            ? $"WAIT: starting {active} in {(started - now).TotalSeconds:F1}s"
            : $"TEST NOW: {active}, {packets.Count} packets, {(started.AddSeconds(5) - now).TotalSeconds:F1}s left";
        status.Text = $"{phase}  |  Completed tests: {tests.Count(t => t.Completed)}  |  Errors: {errors.Count}";
    }

    private void RecordError(string error)
    {
        errors.Add(error);
        log.AppendText($"ERROR: {error}\r\n");
        Console.Error.WriteLine(error);
        UpdateStatus();
    }

    private void SaveOnClose(FormClosingEventArgs e)
    {
        if (active is not null)
        {
            RecordError("Window closed before the timed test finished; its result is marked incomplete.");
            FinishTest(false);
        }
        try
        {
            var directory = Path.GetDirectoryName(output)
                ?? throw new InvalidOperationException("Results path has no parent directory.");
            Directory.CreateDirectory(directory);
            File.WriteAllText(output, JsonSerializer.Serialize(
                new CaptureResult(1, devices.Select(d => d.Description).ToArray(), tests, errors),
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Saved {tests.Count} tests to {output}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecordError($"Could not save results: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Results not saved", MessageBoxButtons.OK, MessageBoxIcon.Error);
            e.Cancel = true;
            return;
        }
        timer.Stop();
        timer.Dispose();
    }
}
