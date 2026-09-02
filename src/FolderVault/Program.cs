using FolderVault.App;

namespace FolderVault;

internal static class Program
{
    /// <summary>
    /// Entry point. Every double-click on a locked folder starts a new process, so the first job
    /// is to work out whether we are the one instance that owns the session state, or a courier
    /// whose only task is to hand its command line to that instance and exit.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        using var instance = SingleInstance.TryAcquire();

        if (instance is null)
        {
            // Another copy is already running and holds the unlocked keys and timers.
            SingleInstance.SendToPrimary(args);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        var context = new FolderVaultContext(args);
        instance.StartListening(context.HandleForwardedArgs);

        Application.Run(context);
    }

    /// <summary>
    /// Surfaces a crash instead of vanishing silently. An app that moves people's folders around
    /// must never fail invisibly - the message tells them where to look and that recovery runs on
    /// the next start.
    /// </summary>
    private static void ReportCrash(Exception? exception)
    {
        if (exception is null) return;

        try
        {
            var logPath = Path.Combine(Core.Store.VaultRegistry.DefaultDirectory, "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"{DateTimeOffset.Now:u}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");

            MessageBox.Show(
                $"FolderVault hit an unexpected error:{Environment.NewLine}{Environment.NewLine}{exception.Message}" +
                $"{Environment.NewLine}{Environment.NewLine}Your files are not lost. Any folder caught " +
                $"mid-operation is repaired the next time FolderVault starts.{Environment.NewLine}{Environment.NewLine}" +
                $"Details were written to:{Environment.NewLine}{logPath}",
                "FolderVault", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception)
        {
            // Nothing useful left to do if even reporting fails.
        }
    }
}
