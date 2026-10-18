using CommunityToolkit.Mvvm.ComponentModel;

namespace SoftProof.UI.ViewModels;

/// <summary>One entry in the File ▸ Recent files menu, with an optional pin.</summary>
public sealed class RecentFileEntry : ObservableObject
{
    private bool _pinned;

    public RecentFileEntry(string path, bool pinned = false)
    {
        Path = path;
        _pinned = pinned;
    }

    public string Path { get; }

    public bool Pinned
    {
        get => _pinned;
        set
        {
            if (SetProperty(ref _pinned, value))
                OnPropertyChanged(nameof(DisplayName));
        }
    }

    public string FileName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;

    /// <summary>Pin marker keeps the menu scannable while the tooltip carries the full path.</summary>
    public string DisplayName => Pinned ? "📌 " + FileName : FileName;
}
