using System.Diagnostics;
using System.Security.Principal;
using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace ExpertMouse;

internal static class StartupTask
{
    private static string UserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw new InvalidOperationException("Cannot identify the current Windows account.");
        }
    }
    internal const string NamePrefix = "ExpertMousePro.Remapper-";
    internal static string Name => NamePrefix + UserSid;

    internal static string CreateXml(string executable, string sid)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement Element(string name, params object[] content) => new(ns + name, content);
        var task = Element("Task", new XAttribute("version", "1.2"),
            Element("RegistrationInfo", Element("Description", AppBranding.AppName + " tray remapper for this account.")),
            Element("Triggers", Element("LogonTrigger", Element("Enabled", "true"),
                Element("UserId", sid), Element("Delay", "PT10S"))),
            Element("Principals", Element("Principal", new XAttribute("id", "CurrentUser"),
                Element("UserId", sid), Element("LogonType", "InteractiveToken"),
                Element("RunLevel", "HighestAvailable"))),
            Element("Settings", Element("MultipleInstancesPolicy", "IgnoreNew"),
                Element("DisallowStartIfOnBatteries", "false"), Element("StopIfGoingOnBatteries", "false"),
                Element("ExecutionTimeLimit", "PT0S"), Element("Enabled", "true")),
            Element("Actions", new XAttribute("Context", "CurrentUser"),
                Element("Exec", Element("Command", executable), Element("Arguments", "--tray"),
                    Element("WorkingDirectory", Path.GetDirectoryName(executable)!))));
        return new XDocument(new XDeclaration("1.0", "utf-16", null), task).ToString();
    }

    private static async Task<string> Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.System), "schtasks.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the Windows task scheduler utility.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var text = (await output) + (await error);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Windows task scheduler failed ({process.ExitCode}): {text.Trim()}");
        return text;
    }

    internal static async Task<bool> IsEnabled()
    {
        // Listing all tasks distinguishes a missing task from a query/access failure without localized error parsing.
        var xml = await Run("/Query", "/XML");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var tasks = ParseTasks(xml);
        return tasks.Descendants(ns + "Task").Any(t =>
            t.Element(ns + "RegistrationInfo")?.Element(ns + "URI")?.Value == "\\" + Name &&
            t.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value != "false");
    }

    internal static XDocument ParseTasks(string xml) =>
        XDocument.Parse(Regex.Replace(xml, @"<\?xml\s.*?\?>", "", RegexOptions.Singleline,
            TimeSpan.FromSeconds(1)));

    internal static string[] TasksForExecutable(XDocument document, string executable)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        return document.Descendants(ns + "Task")
            .Where(task => task.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Command")?.Value
                .Equals(executable, StringComparison.OrdinalIgnoreCase) == true)
            .Select(task => task.Element(ns + "RegistrationInfo")?.Element(ns + "URI")?.Value)
            .Where(uri => uri is not null && uri.StartsWith("\\" + NamePrefix, StringComparison.OrdinalIgnoreCase))
            .Select(uri => uri!).ToArray();
    }

    internal static async Task<string> MigrateCurrentUser()
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var document = ParseTasks(await Run("/Query", "/XML"));
        var task = document.Descendants(ns + "Task").SingleOrDefault(t =>
            t.Element(ns + "RegistrationInfo")?.Element(ns + "URI")?.Value == "\\" + Name);
        if (task is null || task.Element(ns + "Settings")?.Element(ns + "Enabled")?.Value == "false")
            return "Windows startup was not enabled; setup did not enable it.";
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the installed executable.");
        var existing = task.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Command")?.Value;
        if (string.Equals(existing, executable, StringComparison.OrdinalIgnoreCase))
            return "The existing Windows startup entry already targets this installation.";
        await SetEnabled(true);
        return "Updated this account's existing enabled startup entry to the installed app.";
    }

    internal static async Task<int> RemoveInstallationTasks()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot identify this installation for startup cleanup.");
        var document = ParseTasks(await Run("/Query", "/XML"));
        var names = TasksForExecutable(document, executable);
        foreach (var name in names)
            await Run("/Delete", "/TN", name, "/F");
        var remaining = ParseTasks(await Run("/Query", "/XML"));
        if (TasksForExecutable(remaining, executable).Length != 0)
            throw new InvalidOperationException("An installation-specific startup entry could not be removed.");
        return names.Length;
    }

    internal static async Task SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            if (await IsEnabled())
                await Run("/Delete", "/TN", Name, "/F");
            return;
        }
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the remapper executable.");
        var temporary = Path.Combine(Path.GetTempPath(), "ExpertMouse-startup-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            await File.WriteAllTextAsync(temporary, CreateXml(executable, UserSid), System.Text.Encoding.Unicode);
            await Run("/Create", "/TN", Name, "/XML", temporary, "/F");
            if (!await IsEnabled())
                throw new InvalidOperationException("Windows did not retain the enabled startup task.");
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
