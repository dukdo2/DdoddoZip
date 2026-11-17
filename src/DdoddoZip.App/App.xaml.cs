using System.Text;
using System.Windows;
using DdoddoZip.App.Services;
using DdoddoZip.App.Views;
using DdoddoZip.Core;

namespace DdoddoZip.App;

/// <summary>
/// Start-up routes:
///   DdoddoZip.exe                          → main window (S1)
///   DdoddoZip.exe &lt;archive&gt;                → open it (S2)
///   DdoddoZip.exe &lt;files...&gt;               → compress window (S3)
///   DdoddoZip.exe --quickzip --list &lt;file&gt;  → right-click commands, see ShellCommandRunner
///   DdoddoZip.exe --register-shell / --unregister-shell [--user]   (default: all users, needs admin)
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, a) =>
        {
            a.Handled = true;
            try { ErrorWindow.ShowException(Current.MainWindow, a.Exception); }
            catch { MessageBox.Show(a.Exception.ToString(), "또또집 오류", MessageBoxButton.OK, MessageBoxImage.Error); }
        };

        var args = e.Args;
        try
        {
            switch (args.FirstOrDefault())
            {
                // Machine-wide (needs admin) unless "--user" is given.
                case "--register-shell":
                    ShellIntegration.Register(machine: !args.Contains("--user"));
                    Shutdown(0);
                    return;
                case "--unregister-shell":
                    ShellIntegration.Unregister(machine: !args.Contains("--user"));
                    Shutdown(0);
                    return;
                case var command when ShellCommandRunner.IsShellCommand(command):
                    RunShellCommand(command!, args[1..]);
                    return;
            }
        }
        catch (Exception ex)
        {
            ErrorWindow.ShowException(null, ex);
            Shutdown(2);
            return;
        }

        _ = Task.Run(TempFiles.SweepStale);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var main = new MainWindow();
        MainWindow = main;
        main.Show();

        var paths = args.Where(a => File.Exists(a) || Directory.Exists(a)).ToList();
        if (paths.Count == 1 && ArchiveTypes.LooksLikeArchive(paths[0]))
            main.OpenArchive(paths[0]);
        else if (paths.Count > 0)
            main.StartCompress(paths);
    }

    private async void RunShellCommand(string command, string[] args)
    {
        try
        {
            await ShellCommandRunner.RunAsync(command, args);
        }
        catch (Exception ex)
        {
            ErrorWindow.ShowException(null, ex);
        }
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TempFiles.Cleanup();
        base.OnExit(e);
    }
}
