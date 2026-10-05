using System.Windows;
using System.Windows.Threading;
using MediaForge.Services;

namespace MediaForge;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        MediaForgeLogger.Info("MediaForge starting.");
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MediaForgeLogger.Error("DispatcherUnhandledException", e.Exception);
        MessageBox.Show(
            $"MediaForge encountered an unexpected error.\n\n{e.Exception.Message}\n\nLog:\n{MediaForgeLogger.MainLogPath}",
            "MediaForge Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            MediaForgeLogger.Error($"UnhandledException (terminating={e.IsTerminating})", ex);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        MediaForgeLogger.Error("UnobservedTaskException", e.Exception);
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        MediaForgeLogger.Info($"MediaForge exiting. Code={e.ApplicationExitCode}");
        base.OnExit(e);
    }
}
