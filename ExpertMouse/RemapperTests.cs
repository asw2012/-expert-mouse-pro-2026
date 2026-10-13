using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;

namespace ExpertMouse;

internal static class RemapperTests
{
    private sealed class FakeOutput : IInputOutput
    {
        internal List<string> Events { get; } = [];
        internal HashSet<ushort> PhysicalKeys { get; } = [];
        internal string? FailOn { get; set; }
        public bool IsKeyDown(ushort key) => PhysicalKeys.Contains(key);
        private void Emit(string value)
        {
            if (value == FailOn)
                throw new Win32Exception(5, "Simulated rejected output.");
            Events.Add(value);
        }
        public void Key(ushort key, bool down) => Emit($"K{key}:{down}");
        public void Mouse(OutputButton button, bool down) => Emit($"M{button}:{down}");
        public void Launch(string path) => Emit("Launch:" + path);
    }
    private static AppSettings With(params Binding[] changed)
    {
        var settings = Mappings.Defaults();
        foreach (var binding in changed)
            settings.Bindings[settings.Bindings.FindIndex(b => b.Control == binding.Control)] = binding;
        return settings;
    }
    internal static int Run()
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            count++;
            if (!condition) throw new InvalidOperationException("FAIL: " + label);
        }
        void Invalid(Action action, string label)
        {
            var rejected = false;
            try { action(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, label);
        }
        Check(Marshal.SizeOf<InputNative.Input>() == 40, "x64 SendInput union layout");
        using (var grid = new DataGridView { AllowUserToAddRows = false })
        {
            grid.Columns.Add("control", "Control");
            grid.Columns.Add("action", "Action");
            grid.Columns.Add("value", "Value");
            var index = grid.Rows.Add("button", "PassThrough", "");
            var row = grid.Rows[index];
            MappingForm.SetValueCell(row, MappingAction.PassThrough, "");
            Check(row.Cells[2].ReadOnly, "attached pass-through grid cell is read-only");
            MappingForm.SetValueCell(row, MappingAction.Shortcut, "F13");
            Check(!row.Cells[2].ReadOnly && (string?)row.Cells[2].Value == "F13", "shortcut grid cell is editable");
            MappingForm.SetValueCell(row, MappingAction.MouseButton, "Right");
            Check(row.Cells[2] is DataGridViewComboBoxCell && !row.Cells[2].ReadOnly, "mouse action creates an attached editable dropdown");
        }
        Check(Marshal.SizeOf<InputNative.MouseHookData>() == 32, "x64 mouse hook structure layout");
        Check(InputNative.KeyEvent(0x25, false).Keyboard.Flags == 3, "extended arrow key release flags");
        Check(InputNative.MouseEvent(OutputButton.Forward, true).Mouse.Data == 2, "XBUTTON2 injected data");
        Check(DeviceSurvey.IsSoleExpertMouse([@"HID\VID_047D&PID_1002"]), "sole trackball allowed");
        Check(!DeviceSurvey.IsSoleExpertMouse([@"HID\VID_047D&PID_1002", "another mouse"]), "second pointer blocks primary remapping");
        Check(!DeviceSurvey.IsSoleExpertMouse([]), "no pointer blocks primary remapping");
        Check(ShortcutKeys.Parse("Ctrl+Shift+Esc").SequenceEqual(new ushort[] { 17, 16, 27 }), "shortcut modifier parsing");
        Check(ShortcutKeys.Parse("Win+E").SequenceEqual(new ushort[] { 91, 69 }), "Windows key shortcut");
        Check(ShortcutKeys.Parse("F13").SequenceEqual(new ushort[] { 124 }), "extra function key shortcut");
        Invalid(() => ShortcutKeys.Parse("Ctrl+Ctrl+C"), "duplicate modifier rejected");
        Invalid(() => ShortcutKeys.Parse("Ctrl+"), "empty shortcut component rejected");
        Invalid(() => ShortcutKeys.Parse("A+B"), "multiple non-modifier keys rejected");
        Invalid(() => ShortcutKeys.Parse("Ctrl+Alt+Delete"), "unsupported secure attention sequence rejected");
        Invalid(() => Mappings.Validate(new(1, [])), "missing mappings rejected");
        Invalid(() => Mappings.Validate(With(new Binding(ControlId.Direct1, MappingAction.Shortcut, "Ctrl+"))), "invalid configured shortcut rejected");
        Invalid(() => Mappings.Validate(With(new Binding(ControlId.Direct1, MappingAction.LaunchApplication, "relative.exe"))), "relative app path rejected");
        Invalid(() => ExpertReport.Parse("0000"), "wrong hardware report length rejected");
        Invalid(() => ExpertReport.Parse("GG00000000"), "invalid hardware report hex rejected explicitly");
        Invalid(() => ExpertReport.Parse("2000000000"), "unknown main button bits rejected");
        Check(ExpertReport.Parse("1F0000FF3F") == new ExpertReport(2047, -1), "all eleven button bits and negative wheel decode");
        var tracker = new ButtonTracker();
        Check(tracker.Accept(new(32, 0)).Count == 0 && !tracker.Synced, "already-held launch button ignored until release baseline");
        tracker.Accept(new(0, 0));
        Check(tracker.Synced, "released baseline synchronizes");
        Check(tracker.Accept(new(32, 0)).SequenceEqual(new[] { (ControlId.Direct1, true) }), "launch press edge");
        Check(tracker.Accept(new(32, 0)).Count == 0, "duplicate report does not repeat shortcut");
        Check(tracker.Accept(new(0, 0)).SequenceEqual(new[] { (ControlId.Direct1, false) }), "launch release edge");

        var output = new FakeOutput();
        var engine = new MappingEngine(With(new Binding(ControlId.LowerLeft, MappingAction.MouseButton, "Right")), output);
        var policy = new MousePolicy(engine);
        Check(policy.Process(0x201, 0, true, true), "remapped down suppresses original click");
        Check(policy.Process(0x202, 0, true, true), "remapped up suppresses original release");
        Check(output.Events.SequenceEqual(["MRight:True", "MRight:False"]), "mapped mouse drag down/up output");
        output.Events.Clear();
        var queued = new Queue<Action>();
        var deferredPolicy = new MousePolicy(engine, action => queued.Enqueue(action));
        Check(deferredPolicy.Process(0x201, 0, true, true) && output.Events.Count == 0,
            "hook suppresses original before any SendInput side effects");
        Check(deferredPolicy.Process(0x202, 0, true, true) && queued.Count == 2,
            "deferred suppression preserves a quick down/up pair");
        while (queued.TryDequeue(out var action)) action();
        Check(output.Events.SequenceEqual(["MRight:True", "MRight:False"]), "deferred mapped outputs execute in order");
        output.Events.Clear();
        policy.Process(0x201, 0, true, true);
        engine.ReleaseAll();
        Check(policy.Process(0x202, 0, false, true), "paused release remains suppressed");
        Check(output.Events.SequenceEqual(["MRight:True", "MRight:False"]), "pause releases held output once");
        output.Events.Clear();
        policy.Process(0x201, 0, true, true);
        Check(!policy.Process(0x204, 0, true, false), "new other pointer event passes through");
        Check(output.Events.SequenceEqual(["MRight:True", "MRight:False"]), "other pointer cancels primary held output");
        Check(!policy.Process(0x202, 0, true, false), "other pointer release not consumed");
        output.Events.Clear();
        engine.Down(ControlId.LowerLeft);
        output.FailOn = "MRight:False";
        var releaseFailed = false;
        try { engine.ReleaseAll(); } catch (AggregateException) { releaseFailed = true; }
        Check(releaseFailed, "rejected output release is surfaced");
        output.FailOn = null;
        engine.ReleaseAll();
        Check(output.Events.SequenceEqual(["MRight:True", "MRight:False"]), "failed output release is retained and retried");
        engine.Apply(Mappings.Defaults());
        policy = new(engine);
        Check(!policy.Process(0x201, 0, true, true), "pass-through down unchanged");
        engine.Apply(With(new Binding(ControlId.LowerLeft, MappingAction.Disabled)));
        Check(!policy.Process(0x202, 0, true, true), "save during passed-through hold preserves native up");

        output = new();
        engine = new(With(new(ControlId.Direct1, MappingAction.MouseButton, "Left"),
            new(ControlId.Direct2, MappingAction.MouseButton, "Left")), output);
        engine.Down(ControlId.Direct1);
        engine.Down(ControlId.Direct2);
        engine.Up(ControlId.Direct1);
        Check(output.Events.SequenceEqual(["MLeft:True"]), "overlapping mappings do not prematurely release same output");
        engine.Up(ControlId.Direct2);
        Check(output.Events.SequenceEqual(["MLeft:True", "MLeft:False"]), "last mapped owner releases shared output");
        output.Events.Clear();
        engine.Down(ControlId.Direct1);
        engine.ReleaseAll(primaryOnly: true);
        Check(output.Events.SequenceEqual(["MLeft:True"]), "another pointer does not release a DirectLaunch drag");
        engine.Apply(Mappings.Defaults());
        Check(output.Events.SequenceEqual(["MLeft:True", "MLeft:False"]), "configuration change releases old bindings");

        output = new();
        engine = new(With(new Binding(ControlId.Direct1, MappingAction.Shortcut, "Ctrl+C")), output);
        engine.Down(ControlId.Direct1);
        Check(output.Events.SequenceEqual(["K17:True", "K67:True", "K67:False", "K17:False"]), "shortcut releases keys in reverse order");
        output.Events.Clear();
        output.PhysicalKeys.Add(17);
        engine.Down(ControlId.Direct1);
        Check(output.Events.SequenceEqual(["K67:True", "K67:False"]), "physical modifier is not released");
        output.PhysicalKeys.Clear();
        output.Events.Clear();
        output.FailOn = "K67:True";
        var failed = false;
        try { engine.Down(ControlId.Direct1); } catch (AggregateException) { failed = true; }
        Check(failed && output.Events.SequenceEqual(["K17:True", "K17:False"]), "partially failed shortcut releases accepted modifiers");
        output.FailOn = null;
        output.Events.Clear();
        engine.Apply(With(new Binding(ControlId.WheelUp, MappingAction.MouseButton, "Middle")));
        policy = new(engine);
        Check(policy.Process(0x20A, 120u << 16, true, true), "mapped wheel consumes original scroll");
        Check(output.Events.SequenceEqual(["MMiddle:True", "MMiddle:False"]), "wheel mouse action taps rather than holds");
        Check(MouseRemapper.IsSynthetic(new() { Flags = 1 }), "injected mouse output does not recurse");
        Check(MouseRemapper.IsSynthetic(new() { Extra = 0xFF515780 }), "touch-emulated click passes through");
        Check(!MouseRemapper.IsSynthetic(new()), "physical mouse remains eligible");

        var xml = StartupTask.CreateXml(@"C:\Apps & Tools\ExpertMouse.exe", "S-1-5-21-123");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var task = XDocument.Parse(xml);
        Check(task.Descendants(ns + "Command").Single().Value == @"C:\Apps & Tools\ExpertMouse.exe", "startup executable path XML escaped");
        Check(task.Descendants(ns + "LogonType").Single().Value == "InteractiveToken" &&
            task.Descendants(ns + "RunLevel").Single().Value == "HighestAvailable", "startup runs as current user, not SYSTEM");
        Check(task.Descendants(ns + "Arguments").Single().Value == "--tray", "startup opens in tray");
        var owned = new XElement(task.Root!);
        owned.Element(ns + "RegistrationInfo")!.Add(new XElement(ns + "URI", "\\" + StartupTask.NamePrefix + "S-1-5-21-123"));
        var unrelated = new XElement(owned);
        unrelated.Element(ns + "RegistrationInfo")!.Element(ns + "URI")!.Value = "\\AnotherApplication";
        var oldInstallation = new XElement(owned);
        oldInstallation.Element(ns + "Actions")!.Element(ns + "Exec")!.Element(ns + "Command")!.Value = @"C:\Old\ExpertMouse.exe";
        var tasks = new XDocument(new XElement("Tasks", owned, unrelated, oldInstallation));
        Check(StartupTask.TasksForExecutable(tasks, @"C:\Apps & Tools\ExpertMouse.exe").Length == 1,
            "uninstall cleanup only selects matching app task and executable");
        Check(StartupTask.TasksForExecutable(tasks, @"C:\Elsewhere\ExpertMouse.exe").Length == 0,
            "uninstall leaves other installations untouched");
        Check(StartupTask.ParseTasks("<Tasks><?xml version=\"1.0\"?>" + xml + "</Tasks>")
            .Descendants(ns + "Task").Count() == 1, "parse schtasks per-task XML declarations");
        var temporary = Path.Combine(Path.GetTempPath(), "ExpertMouse-settings-test-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var config = With(new Binding(ControlId.Direct6, MappingAction.Shortcut, "Ctrl+Alt+F6"));
            SettingsStore.Save(config, temporary);
            Check(SettingsStore.Load(temporary).Bindings.SequenceEqual(config.Bindings), "settings persist and round trip");
            File.WriteAllText(temporary, "{\"Version\":99,\"Bindings\":[]}");
            Invalid(() => SettingsStore.Load(temporary), "invalid saved settings are not silently defaulted");
            File.WriteAllText(temporary, "{\"Version\":1}");
            var missingRejected = false;
            try { SettingsStore.Load(temporary); } catch (JsonException) { missingRejected = true; }
            Check(missingRejected, "missing required settings properties rejected");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return count;
    }

    internal static void VerifyCapture(string path)
    {
        var capture = JsonSerializer.Deserialize<UsbCaptureResult>(File.ReadAllText(path))
            ?? throw new InvalidOperationException("No USB capture result.");
        if (capture.Errors.Count != 0 || capture.Target is null)
            throw new InvalidOperationException("Capture contains errors or no verified USB target.");
        var covered = new HashSet<ControlId>();
        foreach (var test in capture.Tests.Where(t => t.Completed && t.Reports.Count != 0))
        {
            var index = Array.IndexOf(CaptureForm.TestLabels, test.Label);
            if (index < 0)
                throw new InvalidOperationException("Unrecognized capture label: " + test.Label);
            var control = Mappings.Controls[index];
            var parsed = test.Reports.Select(r => ExpertReport.Parse(r.Hex)).ToArray();
            if (test.Reports.Any(r => r.Address != capture.Target.Address || r.Bus != capture.Target.Bus || r.Endpoint != 0x81))
                throw new InvalidOperationException("Unexpected USB source in labeled reports.");
            if (control.Mask != 0)
            {
                if (parsed.Length != 2 || parsed[0].Buttons != control.Mask || parsed[1].Buttons != 0)
                    throw new InvalidOperationException("Captured press/release does not match " + control.Label);
            }
            else if (parsed.Any(r => r.Wheel != (control.Id == ControlId.WheelUp ? 1 : -1)))
                throw new InvalidOperationException("Captured wheel direction does not match " + control.Label);
            covered.Add(control.Id);
        }
        if (covered.Count != 13)
            throw new InvalidOperationException($"Only {covered.Count}/13 captured controls verified.");
        Console.WriteLine("PASS: Remapper protocol matches all 13 controls in the user's saved hardware capture.");
    }
}
