using System.Windows;

namespace DdoddoZip.App.Views;

/// <summary>S6: asks for an archive password.</summary>
public partial class PasswordWindow : Window
{
    private bool _syncing;

    private PasswordWindow()
    {
        InitializeComponent();
    }

    public static string? Ask(Window? owner, string archivePath, bool previousWasWrong)
    {
        var w = new PasswordWindow();
        if (owner is { IsVisible: true }) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.FileName.Text = Path.GetFileName(archivePath);
        w.WrongText.Visibility = previousWasWrong ? Visibility.Visible : Visibility.Collapsed;
        w.Loaded += (_, _) => w.Hidden.Focus();
        return w.ShowDialog() == true ? w.Hidden.Password : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Hidden.Password.Length == 0)
        {
            Hidden.Focus();
            return;
        }
        DialogResult = true;
    }

    private void Show_Changed(object sender, RoutedEventArgs e)
    {
        bool show = ShowToggle.IsChecked == true;
        Visible.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Hidden.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (show) Visible.Focus(); else Hidden.Focus();
    }

    private void Hidden_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Visible.Text = Hidden.Password;
        _syncing = false;
    }

    private void Visible_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        Hidden.Password = Visible.Text;
        _syncing = false;
    }
}
