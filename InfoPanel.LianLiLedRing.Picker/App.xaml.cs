using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace InfoPanel.LianLiLedRing.Picker
{
    public partial class App : Application
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "InfoPanel", "logs", "ledring-picker.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            // The picker runs as its own process launched by the plugin. If
            // anything throws (theme load, binding, etc.) log it so failures are
            // diagnosable instead of a silent/crashing window.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;

            Log($"Startup. args=[{string.Join(" ", e.Args)}]");
            base.OnStartup(e);
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
