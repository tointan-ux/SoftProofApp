using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace SoftProof.UI.Services;

/// <summary>
/// WPF implementation of <see cref="IFileDialogService"/> using Microsoft.Win32.OpenFileDialog.
/// </summary>
public sealed class FileDialogService : IFileDialogService
{
    public IReadOnlyList<string>? ShowOpenFilesDialog(
        string title,
        string filter,
        string? initialDirectory = null,
        bool multiselect = true)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            Multiselect = multiselect,
            CheckFileExists = true,
            CheckPathExists = true
        };

        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;

        var owner = Application.Current?.MainWindow;
        var accepted = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();

        return accepted == true ? dialog.FileNames : null;
    }
}
