using System.Windows;
using System.Windows.Threading;
using System.IO;

namespace Clearspace;

public partial class App : Application
{
    private static readonly object ErrorLock = new();
    private static string? _lastErrorSignature;
    private static DateTime _lastErrorAt;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Several handlers in this app are async void (event signatures require it),
        // so an exception inside one would otherwise tear the process down with no
        // message at all. Surfacing it keeps failures diagnosable instead of fatal.
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
                Report(exception, "Background error");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            Report(args.Exception, "Unobserved task error");
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report(e.Exception, "Clearspace hit an error");

        // Keep running. A failed listing or shell call should not end the session.
        e.Handled = true;
    }

    private static void Report(Exception exception, string title)
    {
        System.Diagnostics.Debug.WriteLine($"{title}: {exception}");

        var detail = exception is AggregateException aggregate
            ? aggregate.Flatten().InnerException ?? aggregate
            : exception;

        // Keep a small local record as well as the dialog. Startup failures can
        // occur before a window exists, in which case the dialog has nowhere
        // useful to appear. The log is best-effort and never affects browsing.
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Clearspace", "Logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(
                Path.Combine(folder, "errors.log"),
                $"[{DateTime.Now:O}] {title}{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Reporting must never create a second error.
        }

        // A layout exception can be raised by several queued WPF measure passes.
        // One dialog is useful; a stack of identical dialogs is not.
        var signature = $"{detail.GetType().FullName}|{detail.Message}";
        lock (ErrorLock)
        {
            var now = DateTime.UtcNow;
            if (signature == _lastErrorSignature && now - _lastErrorAt < TimeSpan.FromSeconds(5))
                return;

            _lastErrorSignature = signature;
            _lastErrorAt = now;
        }

        MessageBox.Show(
            $"{detail.GetType().Name}\n\n{detail.Message}\n\n{detail.StackTrace}",
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }
}
