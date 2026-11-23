using System.Text;
using System.Windows;
using DdoddoZip.App.Views;
using DdoddoZip.Core;

namespace DdoddoZip.App.Services;

/// <summary>
/// Runs the Explorer right-click commands without the main window:
///   --quickzip / --quick7z      compress next to the selection, automatic name
///   --compress                  compress window (S3)
///   --extract-here              extract straight into the archive's folder
///   --extract-folder            extract into a folder named after each archive
///   --extract                   extract window (S4)
/// The selection comes as "--list &lt;file&gt;" (UTF-8, one path per line) or as plain arguments.
/// </summary>
public static class ShellCommandRunner
{
    public static bool IsShellCommand(string? arg) => arg is
        "--quickzip" or "--quick7z" or "--compress" or "--extract-here" or "--extract-folder" or "--extract";

    public static async Task RunAsync(string command, string[] args)
    {
        var paths = ReadPaths(args);
        if (paths.Count == 0) return;

        switch (command)
        {
            case "--quickzip": await QuickCompress(paths, ArchiveKind.Zip); break;
            case "--quick7z": await QuickCompress(paths, ArchiveKind.SevenZip); break;
            case "--compress": await CompressWithDialog(paths); break;
            case "--extract-here": await ExtractAll(paths, FolderMode.Never, OverwriteMode.Ask, null, openAfter: false); break;
            case "--extract-folder": await ExtractAll(paths, FolderMode.Always, OverwriteMode.Ask, null, openAfter: false); break;
            case "--extract": await ExtractWithDialog(paths); break;
        }
    }

    private static List<string> ReadPaths(string[] args)
    {
        var paths = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--list" && i + 1 < args.Length)
            {
                var list = args[++i];
                try
                {
                    paths.AddRange(File.ReadAllLines(list, Encoding.UTF8).Where(l => !string.IsNullOrWhiteSpace(l)));
                    File.Delete(list);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            else
            {
                paths.Add(args[i]);
            }
        }
        return paths;
    }

    private static async Task QuickCompress(List<string> sources, ArchiveKind kind)
    {
        var title = NamePolicy.QuickArchiveBaseName(sources) + ArchiveKindInfo.Extension(kind) + " 압축 중";
        var result = await Operation.RunAsync(null, title,
            (p, _, ct) => Compressor.QuickArchive(sources, kind, p, ct), "압축하지 못했습니다");
        if (result is not null)
            ShellNative.SelectInExplorer(result.OutputPath);
    }

    private static async Task CompressWithDialog(List<string> sources)
    {
        var req = CompressWindow.Ask(null, sources);
        if (req is null) return;
        var result = await Operation.RunAsync(null, $"{Path.GetFileName(req.Destination)} 압축 중",
            (p, _, ct) => Compressor.Compress(sources, req.Destination, req.Options, p, ct), "압축하지 못했습니다");
        if (result is not null)
            ShellNative.SelectInExplorer(result.OutputPath);
    }

    private static async Task ExtractWithDialog(List<string> archives)
    {
        // Open the first archive to fill in the dialog (file count, whether a folder is needed).
        var first = archives[0];
        (int Files, bool SingleRoot, string? Password)? info;
        try
        {
            var interaction = new WpfInteraction(null);
            info = await Task.Run(() =>
            {
                using var s = ArchiveSession.Open(first, null, interaction);
                return ((int, bool, string?)?)(s.Entries.Count(e => !e.IsDirectory), s.HasSingleRoot(), s.Password);
            });
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ArchiveException e)
        {
            ErrorWindow.Show(null, "열 수 없습니다", Operation.Friendly(e), e.ToString(), Path.GetFileName(first));
            return;
        }

        var (files, singleRoot, password) = info!.Value;
        var req = ExtractWindow.Ask(null, first, files, files, singleRoot);
        if (req is null) return;
        await ExtractAll(archives, req.FolderMode, req.Overwrite, req.Destination, req.OpenAfter, password);
    }

    /// <param name="destination">Null = the folder each archive is in.</param>
    private static async Task ExtractAll(
        List<string> archives, FolderMode folderMode, OverwriteMode overwrite, string? destination,
        bool openAfter, string? firstPassword = null)
    {
        string? lastOutput = null;
        for (int i = 0; i < archives.Count; i++)
        {
            var archive = archives[i];
            var name = Path.GetFileName(archive);
            var title = archives.Count > 1 ? $"({i + 1}/{archives.Count}) {name} 푸는 중" : $"{name} 푸는 중";
            var options = new ExtractOptions
            {
                Destination = destination ?? Path.GetDirectoryName(Path.GetFullPath(archive))!,
                FolderMode = folderMode,
                Overwrite = overwrite,
            };
            var openOptions = new ArchiveOpenOptions { Password = i == 0 ? firstPassword : null };

            var result = await Operation.RunAsync(null, title, (p, ia, ct) =>
            {
                using var session = ArchiveSession.Open(archive, openOptions, ia, ct, p);
                var r = session.Extract(options, ia, p, ct);
                return new Outcome(r, session.Entries.Count(e => !e.IsDirectory));
            }, $"{name}: 풀기를 끝내지 못했습니다");

            if (result is null) return; // cancelled or failed: stop the batch
            lastOutput = result.Result.OutputDirectory;
            if (result.Result.HasProblems)
                ErrorWindow.ShowProblems(null, $"{name}: 풀기를 끝내지 못했습니다", result.Result, result.TotalFiles);
        }

        if (openAfter && lastOutput is not null && Directory.Exists(lastOutput))
            ShellNative.OpenWithShell(lastOutput);
    }

    private sealed record Outcome(ExtractResult Result, int TotalFiles);
}
