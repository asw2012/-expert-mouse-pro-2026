using System.ComponentModel;
using System.Text.Json;
using System.Security.Principal;

namespace ExpertMouse;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.SequenceEqual(["--install-migrate-startup"]))
            {
                Console.WriteLine(StartupTask.MigrateCurrentUser().GetAwaiter().GetResult());
                return 0;
            }
            if (args.SequenceEqual(["--uninstall-cleanup"]))
            {
                var count = StartupTask.RemoveInstallationTasks().GetAwaiter().GetResult();
                Console.WriteLine($"Removed {count} startup entries belonging to this executable. Personal settings were preserved.");
                return 0;
            }
            if (args.SequenceEqual(["--self-test"]))
            {
                SelfTests.Run();
                return 0;
            }
            if (args.Length == 2 && args[0] == "--generate-icon")
            {
                if (!Path.IsPathFullyQualified(args[1]))
                    throw new InvalidOperationException("Icon output path must be absolute.");
                Directory.CreateDirectory(Path.GetDirectoryName(args[1])!);
                File.WriteAllBytes(args[1], AppBranding.IconBytes());
                Console.WriteLine("Generated multi-resolution blue M application icon.");
                return 0;
            }
            if (args.Length is 2 or 3 && args[0] == "--theme-preview")
            {
                if (!Path.IsPathFullyQualified(args[1]))
                    throw new InvalidOperationException("Theme preview path must be absolute.");
                if (args.Length == 3 && !Path.IsPathFullyQualified(args[2]))
                    throw new InvalidOperationException("Theme preview settings path must be absolute.");
                ApplicationConfiguration.Initialize();
                using var form = new MappingForm(false, showTray: false);
                form.Populate(SettingsStore.Load(args.Length == 3 ? args[2] : null));
                form.Show();
                form.PerformLayout();
                Application.DoEvents();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine("Rendered the dark settings preview without starting remapping.");
                return 0;
            }
            if (args.Length == 2 && args[0] == "--verify-capture")
            {
                RemapperTests.VerifyCapture(args[1]);
                return 0;
            }
            if (args.SequenceEqual(["--input-test"]))
            {
                InputNative.FreeConsole();
                ApplicationConfiguration.Initialize();
                Application.Run(new InputTestForm());
                return 0;
            }
            if (args.SequenceEqual(["--probe"]))
            {
                var devices = DeviceSurvey.Find();
                Console.WriteLine(JsonSerializer.Serialize(devices.Select(d => d.Description),
                    new JsonSerializerOptions { WriteIndented = true }));
                if (devices.Count == 0)
                {
                    Console.Error.WriteLine("Expert Mouse Pro 047D:1002 is not connected.");
                    return 2;
                }
                return 0;
            }
            if (args.SequenceEqual(["--usb-probe"]))
            {
                var targets = UsbCapture.FindTargets();
                Console.WriteLine(JsonSerializer.Serialize(targets,
                    new JsonSerializerOptions { WriteIndented = true }));
                if (targets.Count == 0)
                    throw new InvalidOperationException("No Expert Mouse Pro was found on the installed USB capture interfaces.");
                return 0;
            }
            if (args.Length == 2 && args[0] == "--usb-capture")
            {
                if (!Path.IsPathFullyQualified(args[1]))
                    throw new InvalidOperationException("USB capture results path must be absolute.");
                ApplicationConfiguration.Initialize();
                Application.Run(new UsbCaptureForm(args[1]));
                return 0;
            }
            if (args.Length == 2 && args[0] == "--capture")
            {
                var devices = DeviceSurvey.Find();
                if (devices.Count == 0)
                    throw new InvalidOperationException("Expert Mouse Pro 047D:1002 is not connected.");
                if (!Path.IsPathFullyQualified(args[1]))
                    throw new InvalidOperationException("Capture results path must be absolute.");
                ApplicationConfiguration.Initialize();
                Application.Run(new CaptureForm(devices, args[1]));
                return 0;
            }
            if (args.Length == 0 || args.SequenceEqual(["--app"]) || args.SequenceEqual(["--tray"]))
            {
                InputNative.FreeConsole();
                using var identity = WindowsIdentity.GetCurrent();
                var admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                if (!admin && InputNative.FindWindowW(null, MappingForm.WindowTitle) is var existing && existing != 0)
                {
                    InputNative.ShowWindow(existing, 9);
                    InputNative.SetForegroundWindow(existing);
                    return 0;
                }
                // Only elevated instances own the mutex, so the visible activation window can hand over cleanly.
                using var mutex = admin ? new Mutex(false, @"Local\ExpertMousePro.Remapper-" + identity.User?.Value) : null;
                var acquired = true;
                try { acquired = mutex?.WaitOne(0) ?? true; }
                catch (AbandonedMutexException)
                {
                    MessageBox.Show("An earlier app instance exited unexpectedly. Its single-instance lock was recovered.",
                        AppBranding.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                if (!acquired)
                {
                    var window = InputNative.FindWindowW(null, MappingForm.WindowTitle);
                    if (window != 0)
                    {
                        InputNative.ShowWindow(window, 9);
                        InputNative.SetForegroundWindow(window);
                    }
                    return 0;
                }
                try
                {
                    ApplicationConfiguration.Initialize();
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                    using var form = new MappingForm(args.SequenceEqual(["--tray"]));
                    Application.ThreadException += (_, e) => form.ReportUnexpectedError(e.Exception);
                    Application.Run(form);
                }
                finally
                {
                    mutex?.ReleaseMutex();
                }
                return 0;
            }
            if (args.Length != 0)
            {
                Console.Error.WriteLine("Usage: ExpertMouse.exe --probe | --usb-probe | --self-test | " +
                    "--capture <absolute results.json> | --usb-capture <absolute results.json> | " +
                    "--verify-capture <results.json> | --generate-icon <absolute App.ico> | " +
                    "--theme-preview <absolute preview.png> [absolute settings.json] | --input-test | --app | --tray | " +
                    "--install-migrate-startup | --uninstall-cleanup");
                return 2;
            }
            return 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException
                                   or UnauthorizedAccessException or JsonException)
        {
            if (args.Length == 0 || args.SequenceEqual(["--app"]) || args.SequenceEqual(["--tray"]))
                MessageBox.Show(ex.Message, AppBranding.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            else
                Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
