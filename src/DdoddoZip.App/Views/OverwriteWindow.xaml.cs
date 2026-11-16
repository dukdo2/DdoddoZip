using System.Windows;
using DdoddoZip.App.Services;
using DdoddoZip.Core;

namespace DdoddoZip.App.Views;

/// <summary>S7: overwrite / skip / rename, with "apply to all".</summary>
public partial class OverwriteWindow : Window
{
    private OverwriteChoice _choice = OverwriteChoice.Cancel;

    private OverwriteWindow()
    {
        InitializeComponent();
    }

    public static OverwriteDecision Ask(Window? owner, OverwriteRequest r)
    {
        var w = new OverwriteWindow();
        if (owner is { IsVisible: true }) w.Owner = owner;
        else { w.WindowStartupLocation = WindowStartupLocation.CenterScreen; w.ShowInTaskbar = true; }

        w.FileName.Text = Path.GetFileName(r.ExistingPath);
        w.OldSize.Text = Fmt.Bytes((ulong)r.ExistingSize);
        w.OldDate.Text = Fmt.Date(r.ExistingModified);
        w.NewSize.Text = Fmt.Bytes(r.NewSize);
        w.NewDate.Text = Fmt.Date(r.NewModified);

        // Highlight whichever copy is newer.
        if (r.NewModified is { } newTime && newTime > r.ExistingModified)
            Highlight(w.NewCard, w.NewLabel, "압축 파일 안의 파일 (더 최신)");
        else if (r.NewModified is { } t && t < r.ExistingModified)
            Highlight(w.OldCard, w.OldLabel, "지금 있는 파일 (더 최신)");

        w.ShowDialog();
        return new OverwriteDecision(w._choice, w.ApplyAll.IsChecked == true);
    }

    private static void Highlight(System.Windows.Controls.Border card, System.Windows.Controls.TextBlock label, string text)
    {
        card.BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["Accent"];
        card.Background = (System.Windows.Media.Brush)Application.Current.Resources["AccentLight"];
        label.Text = text;
    }

    private void Overwrite_Click(object sender, RoutedEventArgs e) => Finish(OverwriteChoice.Overwrite);
    private void Skip_Click(object sender, RoutedEventArgs e) => Finish(OverwriteChoice.Skip);
    private void Rename_Click(object sender, RoutedEventArgs e) => Finish(OverwriteChoice.Rename);

    private void Finish(OverwriteChoice choice)
    {
        _choice = choice;
        Close();
    }
}
