using System.Windows;
using System.Windows.Threading;
using FileViewer.App.Logging;

namespace FileViewer.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Malformed files, exporter I/O failures, etc. must never crash the process (PRS §8).
        FileLogger.Instance.LogError("Unhandled UI-thread exception.", e.Exception);
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "Bloomberg File Viewer",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Non-UI-thread exceptions on a thread the CLR doesn't let us cancel termination for —
        // logged on a best-effort basis so there's a record even though the process may still exit.
        FileLogger.Instance.LogError("Unhandled non-UI-thread exception (process may terminate).", e.ExceptionObject as Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        FileLogger.Instance.LogError("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
