using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ExpertMouse;

internal interface IInputOutput
{
    bool IsKeyDown(ushort key);
    void Key(ushort key, bool down);
    void Mouse(OutputButton button, bool down);
    void Launch(string path);
}

internal static class InputNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int X, Y;
        internal uint Data, Flags, Time;
        internal nuint Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort Key, Scan;
        internal uint Flags, Time;
        internal nuint Extra;
    }
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    internal struct Input
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal MouseInput Mouse;
        [FieldOffset(8)] internal KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseHookData
    {
        internal int X, Y;
        internal uint Data, Flags, Time;
        internal nuint Extra;
    }

    internal delegate nint HookProc(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowsHookExW(int type, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(nint window, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindowW(string? className, string title);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint window, StringBuilder name, int size);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeConsole();

    internal static void Send(Input input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected a mapped input event.");
    }

    internal static bool IsTaskbarOrApp(int x, int y)
    {
        var window = WindowFromPoint(new Point(x, y));
        if (window == 0)
            return false;
        GetWindowThreadProcessId(window, out var process);
        if (process == Environment.ProcessId)
            return true;
        var root = GetAncestor(window, 2);
        var name = new StringBuilder(128);
        GetClassNameW(root, name, name.Capacity);
        return name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow"
            or "TopLevelWindowForOverflowXamlIsland";
    }
    internal static Input KeyEvent(ushort key, bool down)
    {
        var extended = key is >= 0x21 and <= 0x2E or 0x5B or 0x5C or 0x6F or >= 0xA6 and <= 0xB7;
        return new() { Type = 1, Keyboard = new() { Key = key, Flags = (extended ? 1u : 0u) | (down ? 0u : 2u) } };
    }

    internal static Input MouseEvent(OutputButton button, bool down)
    {
        var flags = button switch
        {
            OutputButton.Left => down ? 0x0002u : 0x0004u,
            OutputButton.Right => down ? 0x0008u : 0x0010u,
            OutputButton.Middle => down ? 0x0020u : 0x0040u,
            OutputButton.Back or OutputButton.Forward => down ? 0x0080u : 0x0100u,
            _ => throw new InvalidOperationException("Unknown output mouse button.")
        };
        return new() { Mouse = new() { Flags = flags, Data = button switch
        {
            OutputButton.Back => 1, OutputButton.Forward => 2, _ => 0
        } } };
    }
}

internal sealed class WindowsInputOutput(Action<Exception> reportError) : IInputOutput
{
    public bool IsKeyDown(ushort key) => (InputNative.GetAsyncKeyState(key) & 0x8000) != 0;
    public void Key(ushort key, bool down) => InputNative.Send(InputNative.KeyEvent(key, down));
    public void Mouse(OutputButton button, bool down) => InputNative.Send(InputNative.MouseEvent(button, down));
    public void Launch(string path)
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })
                    ?? throw new InvalidOperationException("Windows did not launch the assigned application.");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                reportError(ex);
            }
        });
    }
}

internal sealed class MappingEngine
{
    private readonly object sync = new();
    private readonly IInputOutput output;
    private Dictionary<ControlId, Binding> bindings;
    private readonly Dictionary<ControlId, OutputButton> held = [];
    private readonly HashSet<ushort> outstandingKeys = [];

    internal MappingEngine(AppSettings settings, IInputOutput output)
    {
        Mappings.Validate(settings);
        bindings = settings.Bindings.ToDictionary(b => b.Control);
        this.output = output;
    }

    internal Binding Get(ControlId control)
    {
        lock (sync) return bindings[control];
    }
    internal void Apply(AppSettings settings)
    {
        Mappings.Validate(settings);
        lock (sync)
        {
            ReleaseAll();
            bindings = settings.Bindings.ToDictionary(b => b.Control);
        }
    }
    internal void Down(ControlId control)
    {
        lock (sync)
        {
            var binding = bindings[control];
            switch (binding.Action)
            {
                case MappingAction.MouseButton:
                    if (held.ContainsKey(control))
                        return;
                    var button = Enum.Parse<OutputButton>(binding.Value);
                    if (!held.Values.Contains(button))
                        output.Mouse(button, true);
                    held.Add(control, button);
                    break;
                case MappingAction.Shortcut:
                    TapShortcut(ShortcutKeys.Parse(binding.Value));
                    break;
                case MappingAction.LaunchApplication:
                    output.Launch(binding.Value);
                    break;
            }
        }
    }
    internal void Up(ControlId control)
    {
        lock (sync)
        {
            if (!held.TryGetValue(control, out var button))
                return;
            if (held.Values.Count(value => value == button) == 1)
                output.Mouse(button, false);
            held.Remove(control);
        }
    }
    internal void Tap(ControlId control)
    {
        Down(control);
        Up(control);
    }
    private void TapShortcut(ushort[] keys)
    {
        var pressed = new List<ushort>();
        Exception? failure = null;
        try
        {
            foreach (var key in keys)
            {
                if (output.IsKeyDown(key))
                    continue;
                output.Key(key, true);
                outstandingKeys.Add(key);
                pressed.Add(key);
            }
        }
        catch (Win32Exception ex)
        {
            failure = ex;
        }
        var errors = new List<Exception>();
        if (failure is not null)
            errors.Add(failure);
        foreach (var key in pressed.AsEnumerable().Reverse())
        {
            try
            {
                output.Key(key, false);
                outstandingKeys.Remove(key);
            }
            catch (Win32Exception ex)
            {
                errors.Add(ex);
            }
        }
        if (errors.Count != 0)
            throw new AggregateException("Mapped shortcut could not be completed; output release will be retried.", errors);
    }
    internal void ReleaseAll(bool primaryOnly = false)
    {
        lock (sync)
        {
            var errors = new List<Exception>();
            foreach (var control in held.Keys.Where(c => !primaryOnly || !Mappings.IsDirect(c)).ToArray())
            {
                try { Up(control); }
                catch (Win32Exception ex) { errors.Add(ex); }
            }
            if (!primaryOnly)
                foreach (var key in outstandingKeys.ToArray())
                {
                    try { output.Key(key, false); outstandingKeys.Remove(key); }
                    catch (Win32Exception ex) { errors.Add(ex); }
                }
            if (errors.Count != 0)
                throw new AggregateException("Windows rejected output cleanup; remaining releases will be retried.", errors);
        }
    }
}
