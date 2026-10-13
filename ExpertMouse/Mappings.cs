using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExpertMouse;

internal enum ControlId
{
    UpperLeft, UpperRight, LowerLeft, LowerRight, WheelClick, WheelUp, WheelDown,
    Direct1, Direct2, Direct3, Direct4, Direct5, Direct6
}
internal enum MappingAction { PassThrough, Disabled, MouseButton, Shortcut, LaunchApplication }
internal enum OutputButton { Left, Right, Middle, Back, Forward }
internal sealed record Binding([property: JsonRequired] ControlId Control,
    [property: JsonRequired] MappingAction Action, [property: JsonRequired] string Value = "");
internal sealed record AppSettings([property: JsonRequired] int Version,
    [property: JsonRequired] List<Binding> Bindings);
internal sealed record ControlDefinition(ControlId Id, string Label, ushort Mask);

internal static class Mappings
{
    internal static readonly ControlDefinition[] Controls =
    [
        new(ControlId.UpperLeft, "Main - upper left", 8),
        new(ControlId.UpperRight, "Main - upper right", 16),
        new(ControlId.LowerLeft, "Main - lower left", 1),
        new(ControlId.LowerRight, "Main - lower right", 2),
        new(ControlId.WheelClick, "Middle / wheel click", 4),
        new(ControlId.WheelUp, "Scroll wheel up", 0),
        new(ControlId.WheelDown, "Scroll wheel down", 0),
        new(ControlId.Direct1, "DirectLaunch 1", 32),
        new(ControlId.Direct2, "DirectLaunch 2", 64),
        new(ControlId.Direct3, "DirectLaunch 3", 128),
        new(ControlId.Direct4, "DirectLaunch 4", 256),
        new(ControlId.Direct5, "DirectLaunch 5", 512),
        new(ControlId.Direct6, "DirectLaunch 6", 1024)
    ];

    internal static bool IsDirect(ControlId control) => control >= ControlId.Direct1;
    internal static AppSettings Defaults() => new(1, Controls.Select(c =>
        new Binding(c.Id, IsDirect(c.Id) ? MappingAction.Disabled : MappingAction.PassThrough)).ToList());

    internal static void Validate(AppSettings settings)
    {
        if (settings.Version != 1 || settings.Bindings is null || settings.Bindings.Any(b => b is null) ||
            settings.Bindings.Count != Controls.Length ||
            settings.Bindings.Select(b => b.Control).Distinct().Count() != Controls.Length)
            throw new InvalidOperationException("Settings must contain one assignment for each of the 13 controls (version 1).");
        foreach (var binding in settings.Bindings)
        {
            if (!Enum.IsDefined(binding.Control) || !Enum.IsDefined(binding.Action) || binding.Value is null)
                throw new InvalidOperationException("Settings contain an unknown control or action.");
            switch (binding.Action)
            {
                case MappingAction.MouseButton:
                    if (!Enum.TryParse<OutputButton>(binding.Value, out var button) || !Enum.IsDefined(button))
                        throw new InvalidOperationException("Mouse action must be Left, Right, Middle, Back, or Forward.");
                    break;
                case MappingAction.Shortcut:
                    ShortcutKeys.Parse(binding.Value);
                    break;
                case MappingAction.LaunchApplication:
                    if (!Path.IsPathFullyQualified(binding.Value) ||
                        !binding.Value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(binding.Value))
                        throw new InvalidOperationException("Choose an existing application .exe with an absolute path.");
                    break;
                default:
                    if (binding.Value.Length != 0)
                        throw new InvalidOperationException("Pass-through and disabled assignments must not contain a value.");
                    break;
            }
        }
    }

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
}

internal static class ShortcutKeys
{
    internal static ushort[] Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Enter a shortcut such as Ctrl+C, Ctrl+Shift+Esc, or F13.");
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        var keys = new List<ushort>();
        for (var i = 0; i < parts.Length; i++)
        {
            var key = parts[i].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => Keys.ControlKey,
                "ALT" => Keys.Menu,
                "SHIFT" => Keys.ShiftKey,
                "WIN" or "WINDOWS" => Keys.LWin,
                "ESC" => Keys.Escape,
                "PGUP" => Keys.PageUp,
                "PGDN" => Keys.PageDown,
                "SPACE" => Keys.Space,
                _ => ParseKey(parts[i])
            };
            var modifier = key is Keys.ControlKey or Keys.Menu or Keys.ShiftKey or Keys.LWin;
            if (modifier != (i < parts.Length - 1) || keys.Contains((ushort)key))
                throw new InvalidOperationException("Use distinct modifiers followed by exactly one key, e.g. Ctrl+Alt+F1.");
            keys.Add((ushort)key);
        }
        if (keys.Contains(17) && keys.Contains(18) && keys[^1] == 46)
            throw new InvalidOperationException("Windows' secure Ctrl+Alt+Delete sequence cannot be simulated. Use Ctrl+Shift+Esc for Task Manager.");
        return keys.ToArray();
    }

    private static Keys ParseKey(string text)
    {
        if (text.Length == 1 && text[0] is >= '0' and <= '9')
            return Keys.D0 + (text[0] - '0');
        if (!Enum.TryParse<Keys>(text, true, out var key) || !Enum.IsDefined(key) ||
            (int)key <= 0 || (int)key > 255 || key is Keys.LButton or Keys.RButton or Keys.MButton
                or Keys.XButton1 or Keys.XButton2 or Keys.LShiftKey or Keys.RShiftKey or Keys.LControlKey
                or Keys.RControlKey or Keys.LMenu or Keys.RMenu or Keys.RWin)
            throw new InvalidOperationException($"Unknown or unsupported shortcut key: {text}");
        return key;
    }
}

internal readonly record struct ExpertReport(ushort Buttons, sbyte Wheel)
{
    internal static ExpertReport Parse(string hex)
    {
        if (hex.Length != 10)
            throw new InvalidOperationException("Expected a five-byte Expert Mouse USB report.");
        byte[] bytes;
        try { bytes = Convert.FromHexString(hex); }
        catch (FormatException ex) { throw new InvalidOperationException("Invalid hexadecimal Expert Mouse report.", ex); }
        if ((bytes[0] & 0xE0) != 0 || (bytes[4] & 0xC0) != 0)
            throw new InvalidOperationException("Expert Mouse report contains unrecognized button bits.");
        return new((ushort)(bytes[0] | (bytes[4] << 5)), unchecked((sbyte)bytes[3]));
    }
}

internal sealed class ButtonTracker
{
    private ushort previous;
    internal bool Synced { get; private set; }
    internal List<(ControlId Control, bool Down)> Accept(ExpertReport report)
    {
        if (!Synced)
        {
            Synced = report.Buttons == 0;
            previous = report.Buttons;
            return [];
        }
        var changed = previous ^ report.Buttons;
        previous = report.Buttons;
        return Mappings.Controls.Where(c => c.Mask != 0 && (changed & c.Mask) != 0)
            .Select(c => (c.Id, (report.Buttons & c.Mask) != 0)).ToList();
    }
}

internal static class SettingsStore
{
    internal static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExpertMousePro");
    internal static string SettingsPath => Path.Combine(DirectoryPath, "settings.json");

    internal static AppSettings Load(string? path = null)
    {
        path ??= SettingsPath;
        if (!File.Exists(path))
            return Mappings.Defaults();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Mappings.Json)
            ?? throw new InvalidOperationException("Settings file is empty or invalid.");
        Mappings.Validate(settings);
        return settings;
    }

    internal static void Save(AppSettings settings, string? path = null)
    {
        Mappings.Validate(settings);
        path ??= SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Mappings.Json));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
