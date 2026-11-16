using System.Windows;
using DdoddoZip.App.Views;
using DdoddoZip.Core;

namespace DdoddoZip.App.Services;

/// <summary>
/// Runs a long archive job on a background thread with the S5 progress window,
/// answering password/overwrite questions on the UI thread, and turning errors into S8.
/// </summary>
public static class Operation
{
    public static async Task<T?> RunAsync<T>(
        Window? owner,
        string title,
        Func<IProgress<ArchiveProgress>, IArchiveInteraction, CancellationToken, T> work,
        string failureTitle) where T : class
    {
        var progressWindow = new ProgressWindow(title);
        if (owner is { IsVisible: true })
        {
            progressWindow.Owner = owner;
            progressWindow.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            owner.IsEnabled = false;
        }

        using var cts = new CancellationTokenSource();
        progressWindow.CancelRequested += cts.Cancel;
        var interaction = new WpfInteraction(progressWindow);
        progressWindow.Show();

        try
        {
            return await Task.Run(() => work(progressWindow, interaction, cts.Token));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ArchiveException e)
        {
            progressWindow.CloseQuietly();
            ErrorWindow.Show(owner, failureTitle, Friendly(e), e.ToString());
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            progressWindow.CloseQuietly();
            ErrorWindow.Show(owner, failureTitle, "파일을 읽거나 쓸 수 없습니다. " + e.Message, e.ToString());
            return null;
        }
        finally
        {
            progressWindow.CloseQuietly();
            if (owner is not null)
            {
                owner.IsEnabled = true;
                owner.Activate();
            }
        }
    }

    public static string Friendly(ArchiveException e) => e.Kind switch
    {
        ArchiveErrorKind.NotArchive => "압축 파일이 아니거나, 손상되었거나, 지원하지 않는 형식입니다.",
        ArchiveErrorKind.WrongPassword => "암호가 맞지 않습니다.",
        ArchiveErrorKind.EngineMissing => "압축 엔진 파일(7z.dll)이 없습니다. 또또집을 다시 설치해 주세요.",
        _ => e.Message,
    };
}

/// <summary>Asks the person through dialogs; safe to call from the worker thread.</summary>
public sealed class WpfInteraction(Window owner) : IArchiveInteraction
{
    public string? AskPassword(string archivePath, bool previousWasWrong) =>
        owner.Dispatcher.Invoke(() => PasswordWindow.Ask(owner, archivePath, previousWasWrong));

    public OverwriteDecision AskOverwrite(OverwriteRequest request) =>
        owner.Dispatcher.Invoke(() => OverwriteWindow.Ask(owner, request));
}
