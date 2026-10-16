namespace SoftProof.UI.Services;

/// <summary>
/// Abstraction over file pickers so view models don't depend on WPF dialogs directly
/// (and can be unit-tested with a fake).
/// </summary>
public interface IFileDialogService
{
    /// <summary>
    /// Shows an "Open" dialog. Returns the selected file paths, or null if the user cancelled.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="filter">Win32 filter string, e.g. "RIP jobs|*.pbuf;*.tif|All files|*.*".</param>
    /// <param name="initialDirectory">Folder to start in (ignored if it doesn't exist).</param>
    /// <param name="multiselect">Allow selecting several files (e.g. one TIFF per separation).</param>
    IReadOnlyList<string>? ShowOpenFilesDialog(
        string title,
        string filter,
        string? initialDirectory = null,
        bool multiselect = true);
}
