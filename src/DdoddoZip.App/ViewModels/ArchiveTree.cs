using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using DdoddoZip.App.Services;
using DdoddoZip.Core;

namespace DdoddoZip.App.ViewModels;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A folder inside the archive (left tree). Folders without their own entry are created on the fly.</summary>
public sealed class FolderNode : Bindable
{
    private bool _isExpanded;
    private bool _isSelected;

    public FolderNode(string name, string path, FolderNode? parent)
    {
        Name = name;
        Path = path;
        Parent = parent;
    }

    public string Name { get; }

    /// <summary>Path inside the archive ("" for the root).</summary>
    public string Path { get; }

    public FolderNode? Parent { get; }
    public ObservableCollection<FolderNode> Children { get; } = [];
    public List<ArchiveEntry> Files { get; } = [];
    public ArchiveEntry? Entry { get; set; }
    public ImageSource? Icon { get; set; }

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public IEnumerable<FolderNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants())
                yield return d;
    }

    public static FolderNode Build(string archiveName, IReadOnlyList<ArchiveEntry> entries, ImageSource? folderIcon)
    {
        var root = new FolderNode(archiveName, "", null) { Icon = folderIcon, IsExpanded = true };
        var map = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase) { [""] = root };

        FolderNode GetFolder(string path)
        {
            if (map.TryGetValue(path, out var node)) return node;
            var cut = path.LastIndexOf('\\');
            var parent = GetFolder(cut < 0 ? "" : path[..cut]);
            node = new FolderNode(cut < 0 ? path : path[(cut + 1)..], path, parent) { Icon = folderIcon };
            parent.Children.Add(node);
            map[path] = node;
            return node;
        }

        foreach (var e in entries)
        {
            if (e.IsDirectory)
            {
                GetFolder(e.Path).Entry = e;
            }
            else
            {
                var cut = e.Path.LastIndexOf('\\');
                GetFolder(cut < 0 ? "" : e.Path[..cut]).Files.Add(e);
            }
        }

        foreach (var n in map.Values)
        {
            var sorted = n.Children.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            n.Children.Clear();
            foreach (var c in sorted) n.Children.Add(c);
        }
        return root;
    }
}

/// <summary>One row of the right-hand list: a folder or a file.</summary>
public sealed class ItemRow
{
    public required string Name { get; init; }
    public required bool IsFolder { get; init; }
    public ImageSource? Icon { get; init; }
    public string TypeName { get; init; } = "";
    public ulong? Size { get; init; }
    public ulong? PackedSize { get; init; }
    public DateTime? Modified { get; init; }
    public bool IsEncrypted { get; init; }
    public FolderNode? Folder { get; init; }
    public ArchiveEntry? Entry { get; init; }

    public string SizeText => Size is { } s ? Fmt.Bytes(s) : "";
    public string PackedText => PackedSize is { } p ? Fmt.Bytes(p) : "";
    public string ModifiedText => Fmt.Date(Modified);
    public string DisplayName => IsEncrypted ? Name + " 🔒" : Name;
}
