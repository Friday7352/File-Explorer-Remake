using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Clearspace.Commands;
using Clearspace.Models;
using Clearspace.Native;
using Clearspace.Services;

namespace Clearspace.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private CancellationTokenSource? _loadCancellation;

    public MainViewModel()
    {
        Navigation = new NavigationService();
        Context = new ExplorerContext { Navigation = Navigation };
        Commands = new CommandManager(Context);

        Navigation.Navigated += async (_, path) => await LoadAsync(path);
        Context.RefreshRequested += async (_, _) => await RefreshAsync();
        Context.SelectionChanged += (_, _) =>
        {
            Commands.RefreshState();
            UpdateStatus();
        };

        Sidebar = BuildQuickAccess();
    }

    public NavigationService Navigation { get; }

    public ExplorerContext Context { get; }

    public CommandManager Commands { get; }

    public ObservableCollection<SidebarEntry> Sidebar { get; }

    // Named properties keep the XAML readable; they all resolve through the registry.
    public RichCommand BackCommand => Commands[CommandCode.NavigateBack];
    public RichCommand ForwardCommand => Commands[CommandCode.NavigateForward];
    public RichCommand UpCommand => Commands[CommandCode.NavigateUp];
    public RichCommand HomeCommand => Commands[CommandCode.NavigateHome];
    public RichCommand RefreshCommand => Commands[CommandCode.Refresh];
    public RichCommand OpenCommand => Commands[CommandCode.OpenItem];
    public RichCommand DeleteCommand => Commands[CommandCode.Delete];
    public RichCommand RenameCommand => Commands[CommandCode.Rename];
    public RichCommand CopyCommand => Commands[CommandCode.CopyItem];
    public RichCommand CutCommand => Commands[CommandCode.CutItem];
    public RichCommand PasteCommand => Commands[CommandCode.PasteItem];
    public RichCommand CopyPathCommand => Commands[CommandCode.CopyPath];
    public RichCommand NewFolderCommand => Commands[CommandCode.NewFolder];
    public RichCommand PropertiesCommand => Commands[CommandCode.ShowProperties];
    public RichCommand TerminalCommand => Commands[CommandCode.OpenTerminal];

    private IReadOnlyList<FileSystemItem> _items = [];
    public IReadOnlyList<FileSystemItem> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    private string _currentPath = string.Empty;
    public string CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (SetProperty(ref _currentPath, value))
            {
                Context.CurrentPath = value;
                OnPropertyChanged(nameof(Breadcrumbs));
            }
        }
    }

    private string _addressText = string.Empty;
    public string AddressText
    {
        get => _addressText;
        set => SetProperty(ref _addressText, value);
    }

    private string _statusText = "Ready";
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    private bool _showHiddenItems;
    public bool ShowHiddenItems
    {
        get => _showHiddenItems;
        set
        {
            if (SetProperty(ref _showHiddenItems, value))
                _ = RefreshAsync();
        }
    }

    private SortColumn _sortColumn = SortColumn.Name;
    public SortColumn SortColumn
    {
        get => _sortColumn;
        private set => SetProperty(ref _sortColumn, value);
    }

    private bool _sortDescending;
    public bool SortDescending
    {
        get => _sortDescending;
        private set => SetProperty(ref _sortDescending, value);
    }

    public IReadOnlyList<Breadcrumb> Breadcrumbs => BuildBreadcrumbs(CurrentPath);

    public void Start()
    {
        Navigation.Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        // Drives are discovered after the window is up. Querying IsReady or
        // VolumeLabel can block for seconds on an empty optical drive or a
        // disconnected network mapping, which is not something to pay for
        // before the first frame.
        _ = LoadDrivesAsync();
    }

    private async Task LoadDrivesAsync()
    {
        List<SidebarEntry> drives;

        try
        {
            drives = await Task.Run(EnumerateDrives);
        }
        catch (Exception)
        {
            return;
        }

        if (drives.Count == 0)
            return;

        // Back on the UI thread, so the bound collection can be touched directly.
        Sidebar.Add(new SidebarEntry("Drives", string.Empty, IsHeader: true));

        foreach (var drive in drives)
            Sidebar.Add(drive);
    }

    public Task RefreshAsync() => LoadAsync(CurrentPath, force: true);

    public void Sort(SortColumn column)
    {
        SortDescending = column == SortColumn && !SortDescending;
        SortColumn = column;

        var sorted = Items.ToList();
        sorted.Sort(new ItemComparer(SortColumn, SortDescending));
        Items = sorted;
    }

    private async Task LoadAsync(string path, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        // Cancel any in-flight listing so fast navigation never queues behind a
        // slow network folder. The previous source is disposed here rather than in
        // its own finally block, because that block runs while this field still
        // references it and Cancel() on a disposed source throws.
        var previous = _loadCancellation;
        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down.
            }

            previous.Dispose();
        }

        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        var token = cancellation.Token;

        CurrentPath = path;
        AddressText = path;
        IsLoading = true;
        StatusText = "Loading…";

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var showHidden = ShowHiddenItems;
            var column = SortColumn;
            var descending = SortDescending;

            var items = await Task.Run(() =>
            {
                var list = DirectoryEnumerator.Enumerate(path, showHidden, token).ToList();
                list.Sort(new ItemComparer(column, descending));
                return list;
            }, token);

            if (token.IsCancellationRequested)
                return;

            // Icons are assigned before the list is bound, so no per-row change
            // notification fires during the initial render.
            IconService.Populate(items);

            Items = items;
            stopwatch.Stop();

            var folders = items.Count(item => item.IsFolder);
            StatusText = items.Count == 0
                ? $"This folder is empty  ·  {stopwatch.ElapsedMilliseconds} ms"
                : $"{items.Count:N0} items  ·  {folders:N0} folders  ·  {stopwatch.ElapsedMilliseconds} ms";
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer navigation.
        }
        catch (UnauthorizedAccessException)
        {
            Items = [];
            StatusText = "You don't have permission to view this folder";
        }
        catch (DirectoryNotFoundException)
        {
            Items = [];
            StatusText = "That folder no longer exists";
        }
        catch (IOException exception)
        {
            Items = [];
            StatusText = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                IsLoading = false;
                Commands.RefreshState();
            }
        }
    }

    private void UpdateStatus()
    {
        var selected = Context.SelectedItems;

        if (selected.Count == 0)
            return;

        if (selected.Count == 1)
        {
            var item = selected[0];
            StatusText = item.IsFolder
                ? $"{item.Name}  ·  folder"
                : $"{item.Name}  ·  {item.SizeText}";
            return;
        }

        var total = selected.Where(item => !item.IsFolder).Sum(item => item.Size);
        StatusText = $"{selected.Count:N0} selected  ·  {FileSystemItem.FormatSize(total)}";
    }

    private static IReadOnlyList<Breadcrumb> BuildBreadcrumbs(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return [];

        var crumbs = new List<Breadcrumb>();
        var current = path;

        while (!string.IsNullOrEmpty(current))
        {
            var name = Path.GetFileName(current);
            if (string.IsNullOrEmpty(name))
                name = current.TrimEnd('\\');

            crumbs.Insert(0, new Breadcrumb(name, current));

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current)
                break;

            current = parent;
        }

        return crumbs;
    }

    private static ObservableCollection<SidebarEntry> BuildQuickAccess()
    {
        // Every path here comes from SHGetFolderPath, which is a registry lookup
        // and never touches the disk, so this is safe to run before the window shows.
        return
        [
            new("Quick access", string.Empty, IsHeader: true),
            new("Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            new("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            new("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            new("Downloads", GetDownloadsPath()),
            new("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            new("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            new("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))
        ];
    }

    private static List<SidebarEntry> EnumerateDrives()
    {
        var entries = new List<SidebarEntry>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;

                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
                entries.Add(new SidebarEntry($"{label} ({drive.Name.TrimEnd('\\')})", drive.RootDirectory.FullName));
            }
            catch (IOException)
            {
                // Drive disappeared between enumeration and query.
            }
            catch (UnauthorizedAccessException)
            {
                // Mapped drive we cannot inspect.
            }
        }

        return entries;
    }

    private static string GetDownloadsPath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidate = Path.Combine(profile, "Downloads");
        return Directory.Exists(candidate) ? candidate : profile;
    }
}

public sealed record Breadcrumb(string Name, string Path);

public sealed record SidebarEntry(string Name, string Path, bool IsHeader = false);
