using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Clearspace.Commands;
using Clearspace.Models;
using Clearspace.Native;
using Clearspace.Services;

namespace Clearspace.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public const string MyPcPath = "clearspace://my-pc";
    public const string NetworkPath = "clearspace://network";
    public const string YourFilesPath = "clearspace://your-files";
    public const string PinnedPath = "clearspace://pinned";
    public const string CategoryPathPrefix = "clearspace://category/";

    private CancellationTokenSource? _loadCancellation;
    private List<SidebarEntry> _driveEntries = [];

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

        Sidebar = new ObservableCollection<SidebarEntry>();
        RebuildSidebar();
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

    private LayoutMode _layout = LayoutMode.Details;
    public LayoutMode Layout
    {
        get => _layout;
        private set
        {
            if (SetProperty(ref _layout, value))
            {
                OnPropertyChanged(nameof(IsGrid));
                OnPropertyChanged(nameof(IsDetails));
            }
        }
    }

    public bool IsGrid => Layout == LayoutMode.Grid;

    public bool IsDetails => Layout == LayoutMode.Details;

    private double _tileScale = 1;
    private bool _restoringTileScale;
    public double TileScale
    {
        get => _tileScale;
        private set
        {
            var clamped = Math.Clamp(value, 0.70, 2.20);
            if (!SetProperty(ref _tileScale, clamped)) return;
            OnPropertyChanged(nameof(TileWidth));
            OnPropertyChanged(nameof(TileHeight));
            OnPropertyChanged(nameof(TilePreviewSize));
            OnPropertyChanged(nameof(TilePreviewAreaHeight));
            OnPropertyChanged(nameof(TileZoomText));

            if (!_restoringTileScale && !string.IsNullOrWhiteSpace(CurrentPath))
                SettingsService.SetFolderTileScale(CurrentPath, clamped);
        }
    }

    public double TileWidth => Math.Ceiling(132 * TileScale);
    public double TilePreviewSize => Math.Ceiling(104 * TileScale);
    public double TilePreviewAreaHeight => Math.Max(118, TilePreviewSize + 14);
    // Labels deliberately remain at a stable font size as tiles zoom. Only the
    // preview surface and its available layout space change.
    // The two-line filename, drive capacity line and the fixed tile margins all
    // need reserved space. Without it drive labels could be clipped at the bottom.
    public double TileHeight => Math.Ceiling(TilePreviewAreaHeight + 104);
    public string TileZoomText => $"{TileScale * 100:N0}%";

    public void AdjustTileScale(double delta) => TileScale += delta;

    private void RestoreTileScale(string path)
    {
        _restoringTileScale = true;
        try
        {
            // A new location starts at the standard 100%. Once changed, its own
            // value is remembered independently from every other grid.
            TileScale = SettingsService.GetFolderTileScale(path) ?? 1;
        }
        finally
        {
            _restoringTileScale = false;
        }
    }

    /// <summary>
    /// Keep an upper safety bound for the in-memory listing. The grid itself is
    /// virtualized, so this is no longer a visual-container limit.
    /// </summary>
    private const int GridItemLimit = 3000;

    /// <summary>Switches the view and remembers the choice for this folder.</summary>
    public void SetLayout(LayoutMode layout)
    {
        if (layout == LayoutMode.Grid && Items.Count > GridItemLimit)
        {
            StatusText = $"Too many items for tiles ({Items.Count:N0}). Staying in details.";
            return;
        }

        if (Layout == layout)
            return;

        Layout = layout;

        // Grid folders deliberately skip the Shell's small list icons during
        // navigation. Resolve them only if the user actually opens Details.
        if (layout == LayoutMode.Details)
            _ = EnsureItemIconsAsync();

        if (!string.IsNullOrEmpty(CurrentPath))
            SettingsService.SetFolderLayout(CurrentPath, layout.ToString());
    }

    public void ToggleLayout()
        => SetLayout(Layout == LayoutMode.Details ? LayoutMode.Grid : LayoutMode.Details);

    private string _statusText = "Ready";
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private string _timingText = string.Empty;
    public string TimingText
    {
        get => _timingText;
        private set
        {
            if (SetProperty(ref _timingText, value))
                OnPropertyChanged(nameof(HasTiming));
        }
    }

    public bool HasTiming => !string.IsNullOrEmpty(TimingText);

    private string _hubTitle = string.Empty;
    public string HubTitle
    {
        get => _hubTitle;
        private set => SetProperty(ref _hubTitle, value);
    }

    private string _hubSummary = string.Empty;
    public string HubSummary
    {
        get => _hubSummary;
        private set => SetProperty(ref _hubSummary, value);
    }

    private string _hubDescription = string.Empty;
    public string HubDescription
    {
        get => _hubDescription;
        private set => SetProperty(ref _hubDescription, value);
    }

    public bool IsHub => !string.IsNullOrEmpty(HubTitle);

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
        Navigation.Navigate(KnownFolders.Profile);

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

        _driveEntries = drives;
        RebuildSidebar();
    }

    public Task RefreshAsync() => LoadAsync(CurrentPath, force: true);

    public void Sort(SortColumn column)
    {
        SortDescending = column == SortColumn && !SortDescending;
        SortColumn = column;

        var sorted = Items.ToList();
        sorted.Sort(new ItemComparer(SortColumn, SortDescending));
        Items = sorted;
        if (!string.IsNullOrWhiteSpace(CurrentPath))
            FolderSnapshotCache.Set(CurrentPath, sorted);
    }

    private async Task LoadAsync(string path, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var navigationTimer = Stopwatch.StartNew();
        var keepCurrentItems = force &&
                               path.Equals(CurrentPath, StringComparison.OrdinalIgnoreCase) &&
                               Items.Count > 0;
        IReadOnlyList<FileSystemItem> snapshot = [];
        var hasSnapshot = !force && FolderSnapshotCache.TryGet(path, out snapshot);
        long? readyMilliseconds = null;

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

        // Invalidate the old folder before changing Items. Visible tiles in the
        // new view can then enqueue valid work as soon as WPF realizes them.
        ThumbnailService.CancelPending();

        CurrentPath = path;
        RestoreTileScale(path);
        AddressText = path;
        IsLoading = true;
        StatusText = hasSnapshot ? "Refreshing cached view…" : "Opening…";
        TimingText = string.Empty;
        ClearHubInfo();

        if (hasSnapshot)
        {
            Items = snapshot;
            Layout = ResolveLayout(path, snapshot);
            readyMilliseconds = navigationTimer.ElapsedMilliseconds;
            TimingText = $"{readyMilliseconds} ms open · refreshing";
        }
        else if (!keepCurrentItems)
        {
            Items = [];
            Layout = ResolveLayout(path, []);
        }

        var stopwatch = navigationTimer;

        try
        {
            if (path.Equals(MyPcPath, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(NetworkPath, StringComparison.OrdinalIgnoreCase))
            {
                await LoadVirtualDrivesAsync(path, stopwatch, token);
                return;
            }

            if (path.Equals(YourFilesPath, StringComparison.OrdinalIgnoreCase) ||
                path.Equals(PinnedPath, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(CategoryPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                await LoadHubAsync(path, stopwatch, token);
                return;
            }

            var showHidden = ShowHiddenItems;
            var column = SortColumn;
            var descending = SortDescending;
            var gridFastPath = ResolveLayout(path, []) == LayoutMode.Grid;
            var showPartial = !hasSnapshot && !keepCurrentItems;
            IProgress<IReadOnlyList<FileSystemItem>> partialProgress = new Progress<IReadOnlyList<FileSystemItem>>(batch =>
            {
                if (!showPartial ||
                    token.IsCancellationRequested ||
                    !path.Equals(CurrentPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Items = batch;
                Layout = ResolveLayout(path, batch);
                readyMilliseconds ??= navigationTimer.ElapsedMilliseconds;
                StatusText = $"Opening… {batch.Count:N0} items ready";
                TimingText = $"{readyMilliseconds} ms first view";

            });

            var items = await Task.Run(() =>
            {
                var list = new List<FileSystemItem>();
                var firstBatchWatch = Stopwatch.StartNew();
                var firstBatchReported = false;

                foreach (var item in DirectoryEnumerator.Enumerate(path, showHidden, token))
                {
                    list.Add(item);

                    // Do not render ordinary folders twice. A 4 ms threshold made
                    // Pictures build a partial grid and immediately throw it away
                    // for the complete grid. Progressive output is reserved for a
                    // genuinely large or slow enumeration.
                    if (showPartial && !firstBatchReported &&
                        (list.Count >= 256 || firstBatchWatch.ElapsedMilliseconds >= 25))
                    {
                        var firstBatch = list.ToList();
                        firstBatch.Sort(new ItemComparer(column, descending));
                        if (!gridFastPath)
                            IconService.Populate(firstBatch);
                        ScalableIconService.PopulateGridPlaceholders(firstBatch);
                        partialProgress.Report(firstBatch);
                        firstBatchReported = true;
                    }
                }

                list.Sort(new ItemComparer(column, descending));
                if (!gridFastPath)
                    IconService.Populate(list);
                ScalableIconService.PopulateGridPlaceholders(list);
                return list;
            }, token);

            if (token.IsCancellationRequested)
                return;

            FolderSnapshotCache.Set(path, items);
            Items = items;
            stopwatch.Stop();
            readyMilliseconds ??= stopwatch.ElapsedMilliseconds;

            Layout = ResolveLayout(path, items);

            var folders = items.Count(item => item.IsFolder);
            var files = items.Count - folders;

            StatusText = items.Count switch
            {
                0 => "This folder is empty",
                _ => $"{folders:N0} folders, {files:N0} files"
            };

            TimingText = readyMilliseconds < stopwatch.ElapsedMilliseconds
                ? $"{readyMilliseconds} ms open · {stopwatch.ElapsedMilliseconds} ms complete"
                : $"{stopwatch.ElapsedMilliseconds} ms";
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

    private async Task LoadVirtualDrivesAsync(string path, Stopwatch stopwatch, CancellationToken token)
    {
        var networkOnly = path.Equals(NetworkPath, StringComparison.OrdinalIgnoreCase);
        var drives = await Task.Run(() => EnumerateDriveItems(networkOnly), token);

        if (token.IsCancellationRequested)
            return;

        IconService.Populate(drives);
        ScalableIconService.PopulateGridPlaceholders(drives);
        FolderSnapshotCache.Set(path, drives);
        Items = drives;
        Layout = LayoutMode.Grid;
        stopwatch.Stop();

        var total = drives.Sum(drive => drive.DriveTotalSpace);
        var available = drives.Sum(drive => drive.DriveAvailableSpace);

        StatusText = networkOnly
            ? drives.Count == 0 ? "No mapped network locations" : $"{drives.Count:N0} network location{(drives.Count == 1 ? string.Empty : "s")}" 
            : $"{drives.Count:N0} drive{(drives.Count == 1 ? string.Empty : "s")}";
        SetHubInfo(
            networkOnly ? "Network" : "This PC",
            drives.Count == 0
                ? networkOnly ? "No mapped locations" : "No local drives found"
                : $"{FileSystemItem.FormatSize(available)} available of {FileSystemItem.FormatSize(total)} total",
            networkOnly
                ? "Mapped network locations available to this computer. Select one to browse it."
                : $"{drives.Count:N0} local drive{(drives.Count == 1 ? string.Empty : "s")} · storage bars show the used space for each drive.");
        TimingText = $"{stopwatch.ElapsedMilliseconds} ms";
    }

    private async Task LoadHubAsync(string path, Stopwatch stopwatch, CancellationToken token)
    {
        var isPinnedHub = path.Equals(PinnedPath, StringComparison.OrdinalIgnoreCase);
        var categoryId = path.StartsWith(CategoryPathPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[CategoryPathPrefix.Length..]
            : null;
        var items = await Task.Run(() => BuildHubItems(isPinnedHub, categoryId), token);

        if (token.IsCancellationRequested)
            return;

        IconService.Populate(items);
        ScalableIconService.PopulateGridPlaceholders(items);
        FolderSnapshotCache.Set(path, items);
        Items = items;
        Layout = LayoutMode.Grid;
        stopwatch.Stop();
        StatusText = items.Count == 0
            ? isPinnedHub ? "No pinned directories yet" : "No locations available"
            : $"{items.Count:N0} location{(items.Count == 1 ? string.Empty : "s")}";
        var categoryName = categoryId is null ? null : SettingsService.GetSidebarSections()
            .FirstOrDefault(section => section.Id.Equals($"category:{categoryId}", StringComparison.OrdinalIgnoreCase))?.Name;
        SetHubInfo(
            categoryName ?? (isPinnedHub ? "Favorites" : "Your files"),
            categoryName is not null
                ? items.Count == 0 ? "No items in this category" : $"{items.Count:N0} saved location{(items.Count == 1 ? string.Empty : "s")}" 
                : isPinnedHub
                ? items.Count == 0 ? "Nothing pinned yet" : $"{items.Count:N0} saved location{(items.Count == 1 ? string.Empty : "s")}" 
                : "Six primary folders",
            categoryName is not null
                ? "Drag Favorites into or out of this category to keep your sidebar organized."
                : isPinnedHub
                ? "Pin any file or folder from its right-click menu to keep it within reach."
                : "Desktop, Documents, Downloads, Pictures, Music, and Videos—your everyday starting points.");
        TimingText = $"{stopwatch.ElapsedMilliseconds} ms";
    }

    private void SetHubInfo(string title, string summary, string description)
    {
        HubTitle = title;
        HubSummary = summary;
        HubDescription = description;
        OnPropertyChanged(nameof(IsHub));
    }

    private void ClearHubInfo()
    {
        if (!IsHub)
            return;

        HubTitle = string.Empty;
        HubSummary = string.Empty;
        HubDescription = string.Empty;
        OnPropertyChanged(nameof(IsHub));
    }

    /// <summary>
    /// Picks the view for a folder: an explicit choice the user made here wins,
    /// otherwise tiles for folders that are mostly pictures, otherwise details.
    /// </summary>
    private static LayoutMode ResolveLayout(string path, IReadOnlyList<FileSystemItem> items)
    {
        var saved = SettingsService.GetFolderLayout(path);

        if (saved is not null && Enum.TryParse<LayoutMode>(saved, out var chosen))
        {
            if (chosen == LayoutMode.Grid && items.Count > GridItemLimit)
                return LayoutMode.Details;

            return chosen;
        }

        if (items.Count <= GridItemLimit &&
            (KnownFolders.IsWithinPictures(path) || MediaTypes.LooksVisual(items)))
            return LayoutMode.Grid;

        return LayoutMode.Details;
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

    private async Task EnsureItemIconsAsync()
    {
        var missing = Items.Where(item => item.Icon is null).ToArray();
        if (missing.Length == 0)
            return;

        var icons = await Task.Run(() => missing.Select(IconService.GetIcon).ToArray());
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        await dispatcher.InvokeAsync(() =>
        {
            for (var index = 0; index < missing.Length; index++)
                missing[index].Icon = icons[index];
        });
    }

    private static IReadOnlyList<Breadcrumb> BuildBreadcrumbs(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return [];

        if (path.Equals(MyPcPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("This PC", MyPcPath)];

        if (path.Equals(NetworkPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("Network", NetworkPath)];

        if (path.Equals(YourFilesPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("Your files", YourFilesPath)];

        if (path.Equals(PinnedPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("Pinned directories", PinnedPath)];

        if (path.StartsWith(CategoryPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var categoryId = path[CategoryPathPrefix.Length..];
            var name = SettingsService.GetSidebarSections()
                .FirstOrDefault(section => section.Id.Equals($"category:{categoryId}", StringComparison.OrdinalIgnoreCase))?.Name ?? "Category";
            return [new Breadcrumb(name, path)];
        }

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

    private static IEnumerable<SidebarEntry> BuildSidebarEntries(IEnumerable<SidebarEntry> drives)
    {
        foreach (var section in SettingsService.GetSidebarSections())
        {
            switch (section.Id)
            {
                case "files":
                    yield return Section(section, YourFilesPath);
                    if (!section.IsCollapsed)
                        foreach (var location in BuildUserFileEntries()) yield return Child(location);
                    break;

                case "favorites":
                    yield return Section(section, PinnedPath, isFavorites: true);
                    if (!section.IsCollapsed)
                        foreach (var pin in SettingsService.GetPins(categoryId: null))
                            yield return new SidebarEntry(pin.Value, pin.Key, IsPinned: true, IsChild: true);
                    break;

                case "this-pc":
                    yield return Section(section, MyPcPath);
                    if (!section.IsCollapsed)
                        foreach (var drive in drives.Where(drive => !drive.IsNetworkDrive)) yield return Child(drive);
                    break;

                case "network":
                    yield return Section(section, NetworkPath);
                    if (!section.IsCollapsed)
                        foreach (var drive in drives.Where(drive => drive.IsNetworkDrive)) yield return Child(drive);
                    break;

                case var _ when section.IsCategory:
                    var categoryId = section.Id["category:".Length..];
                    yield return Section(section, CategoryPathPrefix + categoryId, isCategory: true, categoryId: categoryId);
                    if (!section.IsCollapsed)
                        foreach (var pin in SettingsService.GetPins(categoryId))
                            yield return new SidebarEntry(pin.Value, pin.Key, IsPinned: true, CategoryId: categoryId, IsChild: true);
                    break;
            }
        }
    }

    private static SidebarEntry Section(SidebarSectionInfo section, string path, bool isFavorites = false, bool isCategory = false, string? categoryId = null)
        => new(section.Name, path, IsHeader: true, IsPinnedRoot: isFavorites, IsCategory: isCategory,
            CategoryId: categoryId, IsCollapsed: section.IsCollapsed, IsSection: true, SectionId: section.Id);

    private static SidebarEntry Child(SidebarEntry entry) => entry with { IsChild = true };

    /// <summary>A saved override wins over the known folder location.</summary>
    private static SidebarEntry Entry(string name, string defaultPath)
        => new(name, SettingsService.GetSidebarOverride(name) ?? defaultPath, IsKnownFolder: true);

    /// <summary>Points a sidebar entry somewhere else and remembers it.</summary>
    public void SetSidebarLocation(string name, string path)
    {
        SettingsService.SetSidebarOverride(name, path);
        RebuildSidebar();
    }

    public void ResetSidebarLocation(string name)
    {
        SettingsService.ClearSidebarOverride(name);
        RebuildSidebar();
    }

    public void PinDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(name))
            name = path;

        SettingsService.PinDirectory(path, name);
        RebuildSidebar();
    }

    public void UnpinDirectory(string path)
    {
        SettingsService.UnpinDirectory(path);
        RebuildSidebar();
    }

    public void CreatePinnedCategory(string name)
    {
        SettingsService.CreatePinnedCategory(name);
        RebuildSidebar();
    }

    public void RenamePinnedCategory(string id, string name)
    {
        SettingsService.RenamePinnedCategory(id, name);
        RebuildSidebar();
    }

    public void DeletePinnedCategory(string id)
    {
        SettingsService.DeletePinnedCategory(id);
        RebuildSidebar();
    }

    public void TogglePinnedCategory(string id)
    {
        SettingsService.TogglePinnedCategory(id);
        RebuildSidebar();
    }

    public void ToggleSidebarSection(string id)
    {
        SettingsService.ToggleSidebarSection(id);
        RebuildSidebar();
    }

    public void RenameSidebarSection(string id, string name)
    {
        SettingsService.RenameSidebarSection(id, name);
        RebuildSidebar();
    }

    public void MoveSidebarSection(string sourceId, string targetId, bool placeAfter)
    {
        SettingsService.MoveSidebarSection(sourceId, targetId, placeAfter);
        RebuildSidebar();
    }

    public void MovePinnedDirectory(string path, string? categoryId, string? targetPath, bool placeAfter)
    {
        SettingsService.MovePinnedDirectory(path, categoryId, targetPath, placeAfter);
        RebuildSidebar();
    }

    public void MovePinnedCategory(string sourceId, string beforeId)
    {
        SettingsService.MovePinnedCategory(sourceId, beforeId);
        RebuildSidebar();
    }

    private void RebuildSidebar()
    {
        Sidebar.Clear();

        foreach (var entry in BuildSidebarEntries(_driveEntries))
            Sidebar.Add(entry);
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
                entries.Add(new SidebarEntry(
                    $"{label} ({drive.Name.TrimEnd('\\')})",
                    drive.RootDirectory.FullName,
                    IsNetworkDrive: drive.DriveType == DriveType.Network));
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

    private static List<FileSystemItem> EnumerateDriveItems(bool networkOnly)
    {
        var entries = new List<FileSystemItem>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady ||
                    (networkOnly && drive.DriveType != DriveType.Network) ||
                    (!networkOnly && drive.DriveType == DriveType.Network))
                    continue;

                entries.Add(FileSystemItem.FromDrive(drive));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return entries;
    }

    private static List<FileSystemItem> BuildHubItems(bool pinnedOnly, string? categoryId)
    {
        IEnumerable<SidebarEntry> locations = categoryId is not null
            ? SettingsService.GetPins(categoryId).Select(pin => new SidebarEntry(pin.Value, pin.Key, IsPinned: true, CategoryId: categoryId))
            : pinnedOnly
            ? SettingsService.GetPinnedDirectories()
                .OrderBy(pin => pin.Value, StringComparer.OrdinalIgnoreCase)
                .Select(pin => new SidebarEntry(pin.Value, pin.Key, IsPinned: true))
            : BuildUserFileEntries();

        return locations
            .Select(location => FileSystemItem.FromLocation(location.Path, location.Name))
            .Where(item => item is not null)
            .Cast<FileSystemItem>()
            .ToList();
    }

    private static IEnumerable<SidebarEntry> BuildUserFileEntries()
    {
        yield return Entry("Desktop", KnownFolders.Desktop);
        yield return Entry("Documents", KnownFolders.Documents);
        yield return Entry("Downloads", KnownFolders.Downloads);
        yield return Entry("Pictures", KnownFolders.Pictures);
        yield return Entry("Music", KnownFolders.Music);
        yield return Entry("Videos", KnownFolders.Videos);
    }
}

public sealed record Breadcrumb(string Name, string Path);

public sealed record SidebarEntry(
    string Name,
    string Path,
    bool IsHeader = false,
    bool IsPinned = false,
    bool IsKnownFolder = false,
    bool IsNetworkDrive = false,
    bool IsPinnedRoot = false,
    bool IsCategory = false,
    string? CategoryId = null,
    bool IsCollapsed = false,
    bool IsSection = false,
    string? SectionId = null,
    bool IsChild = false)
{
    public string DisplayName => Name;
    public string CollapseGlyph => IsCollapsed ? "\uE76C" : "\uE70D";
    public bool HasHub => IsSection && !string.IsNullOrWhiteSpace(Path);
    public bool IsNestedPin => IsPinned;
}
