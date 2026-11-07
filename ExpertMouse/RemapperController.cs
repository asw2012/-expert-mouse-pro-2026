using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace ExpertMouse;

internal sealed class RemapperController : IAsyncDisposable
{
    private readonly MappingEngine engine;
    private readonly MouseRemapper hook;
    private UsbCaptureSession? session;
    private ButtonTracker tracker = new();
    private readonly ConcurrentQueue<Exception> faults = new();
    private volatile bool active;
    private volatile bool failed;
    private bool editing = true;
    private bool paused;
    private bool conflict;
    private bool soleMouse;
    private bool unnamedMouse;
    private bool retrying;
    private bool disposed;
    private DateTimeOffset nextHealth;
    private DateTimeOffset nextRetry;
    internal event Action<string>? Notice;
    internal event Action<ControlId, bool>? ButtonActivity;
    internal string State { get; private set; } = "Starting...";
    internal bool Paused => paused;
    internal bool Active => active;

    internal RemapperController(AppSettings settings)
    {
        engine = new(settings, new WindowsInputOutput(Fault));
        hook = new(engine, () => active, Fault);
    }
    private void Fault(Exception error)
    {
        active = false;
        failed = true;
        faults.Enqueue(error);
    }
    internal async Task Restart()
    {
        if (retrying || disposed)
            return;
        retrying = true;
        active = false;
        try
        {
            engine.ReleaseAll();
            if (session is not null)
            {
                var old = session;
                session = null;
                try { await old.DisposeAsync(); }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
                {
                    Notice?.Invoke("ERROR while closing the previous capture: " + ex.Message);
                }
            }
            if (disposed)
                return;
            var targets = UsbCapture.FindTargets();
            if (targets.Count != 1)
                throw new InvalidOperationException($"Found {targets.Count} Expert Mouse Pro devices; exactly one is required.");
            tracker = new();
            session = new(targets[0]);
            failed = false;
            nextHealth = DateTimeOffset.MinValue;
            nextRetry = DateTimeOffset.UtcNow.AddSeconds(10);
            Notice?.Invoke($"Connected to 047D:1002 ({targets[0].CaptureDevice}, address {targets[0].Address}).");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or AggregateException or IOException)
        {
            Fault(ex);
            nextRetry = DateTimeOffset.UtcNow.AddSeconds(10);
        }
        finally
        {
            retrying = false;
        }
    }

    internal void SetEditing(bool value)
    {
        editing = value;
        if (value)
        {
            active = false;
            ReleaseOutputs();
        }
    }
    internal void SetPaused(bool value)
    {
        paused = value;
        active = false;
        ReleaseOutputs();
    }
    internal void Apply(AppSettings settings)
    {
        active = false;
        engine.Apply(settings);
    }
    private void ReleaseOutputs()
    {
        try { engine.ReleaseAll(); }
        catch (AggregateException ex) { Fault(ex); }
    }

    internal async Task Tick()
    {
        if (disposed)
            return;
        var hadFault = false;
        while (faults.TryDequeue(out var error))
        {
            hadFault = true;
            Notice?.Invoke("ERROR: " + error.Message);
        }
        if (hadFault)
            ReleaseOutputs();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var wasActive = active;
            if (now >= nextHealth)
            {
                conflict = IsXMouseRunning();
                var mousePaths = DeviceSurvey.MousePaths();
                unnamedMouse = mousePaths.Any(path => path is null);
                soleMouse = !SystemInformation.TerminalServerSession &&
                    DeviceSurvey.IsSoleExpertMouse(mousePaths);
                if (session is not null && !failed)
                    session.CheckHealth();
                if (!active)
                    engine.ReleaseAll();
                nextHealth = now.AddSeconds(1);
            }
            active = !failed && !editing && !paused && !conflict && session?.Ready == true && tracker.Synced;
            if (wasActive && !active)
                engine.ReleaseAll();
            if (!soleMouse)
                engine.ReleaseAll(primaryOnly: true);
            if (session is not null)
                while (session.TryTake(out var report))
                {
                    if (report is null)
                        continue;
                    if (report.Endpoint != 0x81)
                        throw new InvalidOperationException($"Unexpected Expert Mouse interrupt endpoint 0x{report.Endpoint:X2}.");
                    var edges = tracker.Accept(ExpertReport.Parse(report.Hex));
                    foreach (var (control, down) in edges)
                    {
                        ButtonActivity?.Invoke(control, down);
                        if (!Mappings.IsDirect(control))
                            continue;
                        if (!down)
                            engine.Up(control);
                        else if (active)
                            engine.Down(control);
                    }
                }
            State = failed ? "Capture stopped - normal mouse actions restored; retrying..."
                : conflict ? "Paused: exit X-Mouse Button Control (no uninstall needed)."
                : editing ? "Editing mappings - paused until you hide this window to the tray."
                : paused ? "Paused - normal mouse actions pass through."
                : !tracker.Synced ? "Release all buttons and move the ball once to synchronize."
                : !soleMouse && unnamedMouse ? "DirectLaunch active; main buttons/wheel paused because Windows lists an unnamed pointer."
                : !soleMouse ? "DirectLaunch active; main buttons/wheel paused because another pointer is present."
                : "Active - all configured mappings enabled.";
            if (failed && now >= nextRetry)
                await Restart();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or AggregateException or IOException)
        {
            Fault(ex);
        }
    }

    internal static bool IsXMouseRunning()
    {
        var processes = Process.GetProcessesByName("XMouseButtonControl");
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        active = false;
        var errors = new List<Exception>();
        try { engine.ReleaseAll(); }
        catch (AggregateException ex) { errors.Add(ex); }
        try { hook.Dispose(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { errors.Add(ex); }
        if (session is not null)
        {
            try { await session.DisposeAsync(); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) { errors.Add(ex); }
        }
        if (errors.Count != 0)
            throw new AggregateException("Some remapper resources could not be released.", errors);
    }
}
