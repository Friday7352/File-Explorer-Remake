using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace.Commands;

/// <summary>
/// The shared state every action reads from. Actions never reach into the view model
/// or the window directly; they go through this, which is what keeps them independent
/// of how the list happens to be rendered.
/// </summary>
public sealed class ExplorerContext : ObservableObject
{
    public required NavigationService Navigation { get; init; }

    /// <summary>Native handle of the owning window, needed by shell dialogs.</summary>
    public IntPtr OwnerHandle { get; set; }

    private string _currentPath = string.Empty;
    public string CurrentPath
    {
        get => _currentPath;
        set => SetProperty(ref _currentPath, value);
    }

    private IReadOnlyList<FileSystemItem> _selectedItems = [];
    public IReadOnlyList<FileSystemItem> SelectedItems
    {
        get => _selectedItems;
        set
        {
            if (SetProperty(ref _selectedItems, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasSingleSelection));
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public bool HasSelection => SelectedItems.Count > 0;

    public bool HasSingleSelection => SelectedItems.Count == 1;

    public event EventHandler? SelectionChanged;

    /// <summary>Raised by actions that changed the folder and need the list rebuilt.</summary>
    public event EventHandler? RefreshRequested;

    public void RequestRefresh() => RefreshRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Set by the view; lets actions drive selection and inline rename.</summary>
    public Action<FileSystemItem?>? BeginRename { get; set; }

    public Action? SelectAll { get; set; }

    public Action? ClearSelection { get; set; }

    public Action? InvertSelection { get; set; }

    public Action? FocusAddressBar { get; set; }

    /// <summary>Set by the view; switches between details and tiles.</summary>
    public Action? ToggleLayout { get; set; }

    public string[] SelectedPaths => SelectedItems.Select(item => item.FullPath).ToArray();
}
