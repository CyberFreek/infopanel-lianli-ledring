using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace InfoPanel.LianLiLedRing.Picker
{
    public partial class App : Application
    {
        // Single-instance coordination (per user session).
        private const string MutexName = @"Local\InfoPanel.LianLiLedRing.Picker.SingleInstance";
        private const string ActivateEventName = @"Local\InfoPanel.LianLiLedRing.Picker.Activate";

        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InfoPanel", "logs", "ledring-picker.log");

        private Mutex? _mutex;
        private EventWaitHandle? _activateEvent;

        protected override void OnStartup(StartupEventArgs e)
        {
            // The picker runs as its own process launched by the plugin. If
            // anything throws (theme load, binding, etc.) log it so failures are
            // diagnosable instead of a silent/crashing window.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

            base.OnStartup(e);

            _mutex = new Mutex(true, MutexName, out var createdNew);
            if (!createdNew)
            {
                // A picker is already open. Signal it to come to the front and
                // exit without showing a second window.
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
                    {
                        existing.Set();
                        existing.Dispose();
                    }
                }
                catch
                {
                    // worst case: the user just doesn't get a focus bump
                }

                Log("Second instance detected; activating existing window and exiting.");
                Shutdown();
                return;
            }

            Log($"Startup. args=[{string.Join(" ", e.Args)}]");

            // Listen for later launches asking us to come to the foreground.
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            var listener = new Thread(ActivationListener)
            {
                IsBackground = true,
                Name = "PickerActivationListener",
            };
            listener.Start();

            // No StartupUri, so we create the window ourselves - this lets the
            // second-instance path above bail before any window appears.
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        private void ActivationListener()
        {
            try
            {
                while (_activateEvent!.WaitOne())
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (MainWindow is not { } w) return;
                        if (w.WindowState == WindowState.Minimized)
                        {
                            w.WindowState = WindowState.Normal;
                        }
                        w.Activate();
                        w.Topmost = true;
                        w.Topmost = false;
                        w.Focus();
                    });
                }
            }
            catch
            {
                // event disposed during shutdown
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _activateEvent?.Dispose();
                _mutex?.Dispose();
            }
            catch
            {
                // ignore
            }
            base.OnExit(e);
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log("Dispatcher exception: " + e.Exception);
            try
            {
                MessageBox.Show(
                    "The color picker hit an error:\n\n" + e.Exception.Message +
                    "\n\nDetails were written to:\n" + LogPath,
                    "Lian Li LED Ring - Color Picker",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            catch
            {
                // ignore
            }
            e.Handled = true;
            Shutdown(1);
        }

        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Log("AppDomain exception: " + (e.ExceptionObject?.ToString() ?? "unknown"));
        }

        private static void Log(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch
            {
                // best effort
            }
        }
    }
}
