using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

namespace ExpertMouse;

internal sealed class MousePolicy(MappingEngine engine, Action<Action>? dispatch = null)
{
    private readonly Dictionary<ControlId, bool> decisions = [];
    private bool solePointer = true;
    private void Execute(Action action)
    {
        if (dispatch is null) action();
        else dispatch(action);
    }

    internal bool Process(int message, uint data, bool enabled, bool sole)
    {
        if (!sole && solePointer)
        {
            decisions.Clear();
            Execute(() => engine.ReleaseAll(primaryOnly: true));
        }
        solePointer = sole;
        if (message == 0x020A)
        {
            var delta = unchecked((short)(data >> 16));
            if (!enabled || !sole || delta == 0)
                return false;
            var control = delta > 0 ? ControlId.WheelUp : ControlId.WheelDown;
            if (engine.Get(control).Action == MappingAction.PassThrough)
                return false;
            for (var i = 0; i < Math.Max(1, Math.Abs((int)delta) / 120); i++)
                Execute(() => engine.Tap(control));
            return true;
        }
        var edge = Decode(message, data);
        if (edge is null)
            return false;
        var (button, down) = edge.Value;
        if (!down)
        {
            if (!decisions.TryGetValue(button, out var suppressed))
                return false;
            if (suppressed)
                Execute(() => engine.Up(button));
            decisions.Remove(button);
            return suppressed;
        }
        if (decisions.TryGetValue(button, out var existing))
            return existing;
        var consume = enabled && sole && engine.Get(button).Action != MappingAction.PassThrough;
        decisions.Add(button, consume);
        if (consume)
            Execute(() => engine.Down(button));
        return consume;
    }

    internal static (ControlId Control, bool Down)? Decode(int message, uint data) => message switch
    {
        0x0201 => (ControlId.LowerLeft, true), 0x0202 => (ControlId.LowerLeft, false),
        0x0204 => (ControlId.LowerRight, true), 0x0205 => (ControlId.LowerRight, false),
        0x0207 => (ControlId.WheelClick, true), 0x0208 => (ControlId.WheelClick, false),
        0x020B or 0x020C when (data >> 16) == 1 => (ControlId.UpperLeft, message == 0x020B),
        0x020B or 0x020C when (data >> 16) == 2 => (ControlId.UpperRight, message == 0x020B),
        _ => null
    };

    internal bool WasSuppressed(int message, uint data)
    {
        var edge = Decode(message, data);
        return edge is not null && decisions.GetValueOrDefault(edge.Value.Control);
    }
}

internal sealed class MouseRemapper : IDisposable
{
    private readonly Thread thread;
    private readonly TaskCompletionSource<nint> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<bool> enabled;
    private readonly Action<Exception> fault;
    private readonly MousePolicy policy;
    private readonly MappingEngine engine;
    private readonly InputNative.HookProc callback;
    private readonly ConcurrentQueue<Action> pending = new();
    private nint commandWindow;
    private nint hook;
    private bool disposed;

    internal MouseRemapper(MappingEngine engine, Func<bool> enabled, Action<Exception> fault)
    {
        this.enabled = enabled;
        this.fault = fault;
        this.engine = engine;
        policy = new(engine, Enqueue);
        callback = Hook;
        thread = new Thread(Run) { IsBackground = true, Name = "Expert Mouse input hook" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        started.Task.GetAwaiter().GetResult();
    }

    private sealed class HookWindow(Action drain) : NativeWindow
    {
        internal void Create() => CreateHandle(new CreateParams { Parent = (nint)(-3) });
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x8001)
                Application.ExitThread();
            if (message.Msg == 0x8002)
                drain();
            base.WndProc(ref message);
        }
    }
    private void Run()
    {
        HookWindow? window = null;
        try
        {
            window = new HookWindow(Drain);
            window.Create();
            commandWindow = window.Handle;
            hook = InputNative.SetWindowsHookExW(14, callback, 0, 0);
            if (hook == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Install the mouse remapping hook");
            started.SetResult(window.Handle);
            Application.Run();
        }
        catch (Win32Exception ex)
        {
            if (!started.TrySetException(ex))
                fault(ex);
        }
        finally
        {
            if (hook != 0 && !InputNative.UnhookWindowsHookEx(hook))
                fault(new Win32Exception(Marshal.GetLastWin32Error(), "Remove the mouse remapping hook"));
            window?.DestroyHandle();
        }
    }
    internal static bool IsSynthetic(InputNative.MouseHookData data) =>
        (data.Flags & 3) != 0 || (data.Extra & 0xFFFFFF00u) == 0xFF515700u;

    private void Enqueue(Action action)
    {
        pending.Enqueue(action);
        // Return the hook's suppression decision before SendInput; injecting inside the hook can time out and leak originals.
        if (!InputNative.PostMessageW(commandWindow, 0x8002, 0, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Queue the mapped mouse action");
    }
    private void Drain()
    {
        if (!enabled())
        {
            pending.Clear();
            try { engine.ReleaseAll(primaryOnly: true); }
            catch (AggregateException ex) { fault(ex); }
            return;
        }
        while (pending.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or AggregateException)
            {
                pending.Clear();
                fault(ex);
                break;
            }
        }
    }
    private nint Hook(int code, nint message, nint pointer)
    {
        if (code < 0)
            return InputNative.CallNextHookEx(hook, code, message, pointer);
        var data = Marshal.PtrToStructure<InputNative.MouseHookData>(pointer);
        if (IsSynthetic(data) || (int)message == 0x0200)
            return InputNative.CallNextHookEx(hook, code, message, pointer);
        try
        {
            var sole = !SystemInformation.TerminalServerSession &&
                DeviceSurvey.IsSoleExpertMouse(DeviceSurvey.MousePaths());
            if (policy.Process((int)message, data.Data,
                    enabled() && !InputNative.IsTaskbarOrApp(data.X, data.Y), sole))
                return 1;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or AggregateException)
        {
            var suppressed = policy.WasSuppressed((int)message, data.Data);
            fault(ex);
            if (suppressed)
                return 1;
        }
        return InputNative.CallNextHookEx(hook, code, message, pointer);
    }

    public void Dispose()
    {
        if (disposed)
            return;
        if (!InputNative.PostMessageW(started.Task.GetAwaiter().GetResult(), 0x8001, 0, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Stop the mouse hook thread");
        if (!thread.Join(5000))
            throw new InvalidOperationException("Mouse hook thread did not stop within five seconds.");
        disposed = true;
    }
}
