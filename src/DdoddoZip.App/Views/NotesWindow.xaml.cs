using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using DdoddoZip.App.Services;

namespace DdoddoZip.App.Views;

/// <summary>
/// U4: what changed in one or more releases. Before an update it asks "지금 업데이트";
/// after an update it only shows the changes with [확인].
/// </summary>
public partial class NotesWindow : Window
{
    private NotesWindow()
    {
        InitializeComponent();
    }

    /// <summary>Before updating. True when the person chose to update now.</summary>
    public static bool AskToUpdate(Window? owner, IReadOnlyList<ReleaseInfo> releases)
    {
        var w = Create(owner, $"또또집 {releases[0].Display} 바뀐 점", releases);
        return w.ShowDialog() == true;
    }

    /// <summary>After updating: the changes of the versions just installed.</summary>
    public static void ShowInstalled(Window? owner, IReadOnlyList<ReleaseInfo> releases)
    {
        var w = Create(owner, $"{releases[0].Display}에서 바뀐 점", releases);
        w.InstallNote.Visibility = Visibility.Collapsed;
        w.LaterButton.Visibility = Visibility.Collapsed;
        w.OkButton.Content = "확인";
        w.OkButton.IsCancel = true;
        w.ShowDialog();
    }

    private static NotesWindow Create(Window? owner, string title, IReadOnlyList<ReleaseInfo> releases)
    {
        var w = new NotesWindow { Title = title };
        if (owner is { IsVisible: true }) w.Owner = owner;
        else { w.WindowStartupLocation = WindowStartupLocation.CenterScreen; w.ShowInTaskbar = true; }

        bool several = releases.Count > 1;
        foreach (var r in releases)
        {
            if (several)
            {
                var head = new TextBlock { Margin = new Thickness(0, w.Notes.Children.Count == 0 ? 0 : 10, 0, 2) };
                head.Inlines.Add(new Run(r.Display) { FontWeight = FontWeights.SemiBold });
                if (r.Published is { } p)
                    head.Inlines.Add(new Run($"  {p.ToLocalTime():yyyy년 M월 d일}") { Foreground = (System.Windows.Media.Brush)Application.Current.Resources["Sub"], FontSize = 12 });
                w.Notes.Children.Add(head);
            }
            AddMarkdown(w.Notes, UpdateService.ChangesSection(r.Notes));
        }
        return w;
    }

    /// <summary>Just enough Markdown for release notes: headings, "-"/"*" bullets, **bold**. Links show as text.</summary>
    internal static void AddMarkdown(Panel panel, string text)
    {
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) continue;

            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                tb.FontWeight = FontWeights.SemiBold;
                tb.Margin = new Thickness(0, 6, 0, 2);
                AddInlines(tb, trimmed.TrimStart('#').Trim());
            }
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                var indent = (line.Length - trimmed.Length) / 2;
                var grid = new Grid { Margin = new Thickness(4 + indent * 14, 1, 0, 1) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.Children.Add(new TextBlock { Text = "•", Margin = new Thickness(0, 0, 8, 0) });
                tb.Margin = new Thickness(0);
                AddInlines(tb, trimmed[2..].Trim());
                Grid.SetColumn(tb, 1);
                grid.Children.Add(tb);
                panel.Children.Add(grid);
                continue;
            }
            else
            {
                AddInlines(tb, trimmed);
            }
            panel.Children.Add(tb);
        }
    }

    private static void AddInlines(TextBlock tb, string text)
    {
        // [text](url) → text, `code` → code
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1").Replace("`", "");
        var parts = text.Split("**");
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            tb.Inlines.Add(i % 2 == 1 ? new Bold(new Run(parts[i])) : new Run(parts[i]));
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (OkButton.IsCancel) Close(); // after-update mode
        else DialogResult = true;
    }

    private void Later_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
