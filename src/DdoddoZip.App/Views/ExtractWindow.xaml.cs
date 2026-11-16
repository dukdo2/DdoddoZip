using System.Windows;
using System.Windows.Controls;
using DdoddoZip.App.Services;
using DdoddoZip.Core;
using Microsoft.Win32;

namespace DdoddoZip.App.Views;

public sealed record ExtractRequest(string Destination, FolderMode FolderMode, OverwriteMode Overwrite, bool OpenAfter);

/// <summary>S4: where to extract and how to treat existing files.</summary>
public partial class ExtractWindow : Window
{
    private ExtractRequest? _result;

    private ExtractWindow()
    {
        InitializeComponent();
    }

    /// <param name="singleRoot">True when the chosen items already sit in one top-level folder.</param>
    public static ExtractRequest? Ask(Window? owner, string archivePath, int selectedFiles, int totalFiles, bool singleRoot)
    {
        var w = new ExtractWindow();
        if (owner is { IsVisible: true }) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        w.FolderBox.Text = Path.GetDirectoryName(archivePath) ?? "";
        var folderName = NamePolicy.ArchiveBaseName(archivePath);
        w.MakeFolder.Content = $"압축 파일 이름으로 폴더 만들기 ({folderName}\\)";
        w.MakeFolder.IsChecked = !singleRoot; // a single top folder would otherwise be nested twice
        w.OpenAfter.IsChecked = AppSettings.Current.OpenFolderAfterExtract;
        w.SummaryText.Text = selectedFiles == totalFiles
            ? $"전체 {totalFiles:N0}개를 풉니다"
            : $"선택한 파일 {selectedFiles:N0}개만 풉니다 (전체 {totalFiles:N0}개 중)";

        w.ShowDialog();
        return w._result;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "풀 위치 선택", InitialDirectory = FolderBox.Text };
        if (dlg.ShowDialog(this) == true)
            FolderBox.Text = dlg.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderBox.Text.Trim();
        if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
        {
            MessageBox.Show(this, "풀 위치를 전체 경로로 적어 주세요.", "풀기", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var tag = OverwriteGroup.Children.OfType<RadioButton>().First(r => r.IsChecked == true).Tag as string;
        AppSettings.Current.OpenFolderAfterExtract = OpenAfter.IsChecked == true;
        AppSettings.Current.Save();

        _result = new ExtractRequest(
            folder,
            MakeFolder.IsChecked == true ? FolderMode.Always : FolderMode.Never,
            Enum.Parse<OverwriteMode>(tag!),
            OpenAfter.IsChecked == true);
        Close();
    }
}
