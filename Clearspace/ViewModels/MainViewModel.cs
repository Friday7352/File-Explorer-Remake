using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
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

    // Subfolder search state. The crawl is debounced so typing does not launch a
    // new walk of an entire drive on every keystroke.
    private readonly DispatcherTimer _searchDebounce;
    private CancellationTokenSource? _searchCancellation;
    private IReadOnlyList<FileSystemItem> _localMatches = [];
    private SearchQuery _pendingQuery = SearchQuery.Empty;

    /// <summary>Upper bound on subfolder hits, so a broad query cannot exhaust memory.</summary>
    private const int MaxSearchResults = 10_000;

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
            OnPropertyChanged(nameof(HasSelectedFolders));
            RefreshTagOptions();
        };

        // Definitions can change from the tag dialog; keep the menu in step.
        TagService.Changed += (_, _) => RefreshTagOptions();

        Sidebar = new ObservableCollection<SidebarEntry>();
        RebuildSidebar();
        LoadColumns();
        RefreshTagOptions();

        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _ = RunTreeSearchAsync(_pendingQuery);
        };
    }

    public NavigationService Navigation { get; }

    public ExplorerContext Context { get; }

    public CommandManager Commands { get; }

    public ObservableCollection<SidebarEntry> Sidebar { get; }

    /// <summary>In-app playback, used by the Music folder type.</summary>
    public AudioPlayerViewModel Player { get; } = new();

    /// <summary>Full-window image viewing, used by the Photos folder type.</summary>
    public PhotoViewerViewModel Viewer { get; } = new();

    // ---------- Columns ----------

    /// <summary>The column picker's contents for the current folder type.</summary>
    public ObservableCollection<ColumnOption> ColumnOptions { get; } = [];

    /// <summary>Raised when the visible columns change and the view must rebuild them.</summary>
    public event EventHandler? ColumnsChanged;

    private List<string> _visibleColumns = [];

    public IReadOnlyList<string> VisibleColumns => _visibleColumns;

    private void LoadColumns()
    {
        var profile = FolderProfile == DirectoryViewProfile.Automatic
            ? DirectoryViewProfile.General
            : FolderProfile;

        // This folder's own choice wins; otherwise fall back to what this kind of
        // folder starts with.
        var saved = string.IsNullOrWhiteSpace(CurrentPath)
            ? null
            : SettingsService.GetFolderColumns(CurrentPath);

        _visibleColumns = ColumnCatalog.Sanitise(saved ?? ColumnCatalog.DefaultsFor(profile));

        ColumnOptions.Clear();

        // Catalogue order, not saved order, so the menu never reshuffles as you
        // tick boxes. The saved list still controls the order in the list itself.
        foreach (var info in ColumnCatalog.All)
        {
            ColumnOptions.Add(new ColumnOption(
                info,
                _visibleColumns.Contains(info.Id, StringComparer.OrdinalIgnoreCase),
                OnColumnToggled));
        }

        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnColumnToggled(ColumnOption option)
    {
        if (option.IsVisible)
        {
            if (!_visibleColumns.Contains(option.Id, StringComparer.OrdinalIgnoreCase))
            {
                // Insert in catalogue order so a re-added column returns to a
                // sensible place rather than the far right.
                var target = ColumnCatalog.All
                    .TakeWhile(info => !info.Id.Equals(option.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(info => _visibleColumns.FindIndex(id => id.Equals(info.Id, StringComparison.OrdinalIgnoreCase)))
                    .Where(index => index >= 0)
                    .DefaultIfEmpty(-1)
                    .Max();

                _visibleColumns.Insert(target + 1, option.Id);
            }
        }
        else
        {
            _visibleColumns.RemoveAll(id => id.Equals(option.Id, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(CurrentPath))
            SettingsService.SetFolderColumns(CurrentPath, _visibleColumns);

        ColumnsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops this folder's override and returns to the folder type's defaults.</summary>
    public void ResetColumns()
    {
        if (!string.IsNullOrWhiteSpace(CurrentPath))
            SettingsService.ClearFolderColumns(CurrentPath);

        LoadColumns();
    }

    /// <summary>
    /// Plays a track in the transport bar. Invoked from the play button on a row,
    /// never from double-click: double-click still hands the file to the shell.
    /// </summary>
    public void PlayTrack(FileSystemItem item)
    {
        if (item.IsAudio)
            Player.Play(Items, item);
    }

    /// <summary>Opens the in-app photo reel. Invoked from the button on a tile.</summary>
    public void ViewPhoto(FileSystemItem item)
    {
        if (item.IsImageFile)
            Viewer.Open(Items, item);
    }

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

    // The complete directory snapshot is kept separate from Items. Searching never
    // touches the disk or re-enumerates a directory: it only swaps the displayed
    // slice of this already-sorted in-memory list.
    private IReadOnlyList<FileSystemItem> _directoryItems = [];

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value))
                return;

            OnPropertyChanged(nameof(HasSearch));
            ApplySearchFilter(updateStatus: true);
        }
    }

    public bool HasSearch => !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>
    /// When on, a query naming tags or folder types is answered from the saved
    /// indexes instead of the current listing, so results span every location
    /// Clearspace knows about.
    /// </summary>
    private bool _searchEverywhere;
    public bool SearchEverywhere
    {
        get => _searchEverywhere;
        set
        {
            if (SetProperty(ref _searchEverywhere, value))
                ApplySearchFilter(updateStatus: true);
        }
    }

    // ---------- Tags ----------

    /// <summary>Tag rows for the context menu, with check state for the selection.</summary>
    public ObservableCollection<TagOption> TagOptions { get; } = [];

    private void RefreshTagOptions()
    {
        var paths = Context.SelectedItems.Select(item => item.FullPath).ToArray();

        TagOptions.Clear();

        foreach (var tag in TagService.All)
        {
            var applied = paths.Length > 0 && paths.All(path => TagService.HasTag(path, tag.Id));
            TagOptions.Add(new TagOption(tag, applied, OnTagToggled));
        }
    }

    private void OnTagToggled(TagOption option)
    {
        var paths = Context.SelectedItems.Select(item => item.FullPath).ToArray();
        if (paths.Length == 0)
            return;

        TagService.ToggleForAll(paths, option.Tag.Id);
        RefreshVisibleTags();

        var count = paths.Length == 1 ? "1 item" : $"{paths.Length:N0} items";
        StatusText = option.IsApplied
            ? $"Tagged {count} as {option.Tag.Name}."
            : $"Removed {option.Tag.Name} from {count}.";
    }

    /// <summary>Creates a tag and applies it to the selection in one step.</summary>
    public void CreateTagForSelection(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        var tag = TagService.Create(name);
        var paths = Context.SelectedItems.Select(item => item.FullPath).ToArray();

        if (paths.Length > 0)
        {
            foreach (var path in paths)
                TagService.Assign(path, tag.Id);
        }

        RefreshVisibleTags();
        RefreshTagOptions();
        StatusText = paths.Length == 0
            ? $"Created the {tag.Name} tag."
            : $"Tagged {paths.Length:N0} item{(paths.Length == 1 ? string.Empty : "s")} as {tag.Name}.";
    }

    public void ClearTagsOnSelection()
    {
        var paths = Context.SelectedItems.Select(item => item.FullPath).ToArray();
        if (paths.Length == 0)
            return;

        TagService.ClearTags(paths);
        RefreshVisibleTags();
        RefreshTagOptions();
        StatusText = $"Cleared tags on {paths.Length:N0} item{(paths.Length == 1 ? string.Empty : "s")}.";
    }

    /// <summary>
    /// Removes a tag definition and every assignment of it. The files themselves
    /// are untouched; only the label goes away.
    /// </summary>
    public void DeleteTag(TagDefinition tag)
    {
        TagService.Delete(tag.Id);
        RefreshVisibleTags();
        RefreshTagOptions();

        // A search naming the deleted tag would now be stale.
        if (HasSearch)
            ApplySearchFilter(updateStatus: true);

        StatusText = $"Deleted the {tag.Name} tag.";
    }

    /// <summary>Puts a tag filter into the search box.</summary>
    public void SearchByTag(TagDefinition tag)
    {
        SearchEverywhere = true;
        SearchText = $"tag:{tag.Id}";
    }

    private void RefreshVisibleTags()
    {
        foreach (var item in _directoryItems)
            item.RefreshTags();

        if (!ReferenceEquals(Items, _directoryItems))
        {
            foreach (var item in Items)
                item.RefreshTags();
        }
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

    private DirectoryViewProfile _folderProfile = DirectoryViewProfile.Automatic;
    public DirectoryViewProfile FolderProfile
    {
        get => _folderProfile;
        private set
        {
            if (!SetProperty(ref _folderProfile, value))
                return;

            OnPropertyChanged(nameof(FolderProfileLabel));
            OnPropertyChanged(nameof(IsAutomaticProfile));
            OnPropertyChanged(nameof(IsGeneralProfile));
            OnPropertyChanged(nameof(IsPhotosProfile));
            OnPropertyChanged(nameof(IsMusicProfile));

            // Each folder type carries its own column set.
            LoadColumns();
        }
    }

    public string FolderProfileLabel => FolderProfile switch
    {
        DirectoryViewProfile.Desktop => "Desktop",
        DirectoryViewProfile.Documents => "Documents",
        DirectoryViewProfile.Downloads => "Downloads",
        DirectoryViewProfile.General => "General",
        DirectoryViewProfile.Photos => "Photos",
        DirectoryViewProfile.Music => "Music",
        DirectoryViewProfile.Videos => "Videos",
        _ => "Automatic"
    };

    public bool IsAutomaticProfile => FolderProfile == DirectoryViewProfile.Automatic;
    public bool IsGeneralProfile => FolderProfile == DirectoryViewProfile.General;
    public bool IsPhotosProfile => FolderProfile == DirectoryViewProfile.Photos;
    public bool IsMusicProfile => FolderProfile == DirectoryViewProfile.Music;
    public bool CanSetFolderProfile => !string.IsNullOrWhiteSpace(CurrentPath) &&
                                       !CurrentPath.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase);
    public bool HasSelectedFolders => Context.SelectedItems.Any(item => item.IsStandardFolder);

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

    public void SetFolderProfile(DirectoryViewProfile profile)
    {
        if (!CanSetFolderProfile)
            return;

        SettingsService.SetFolderViewProfile(CurrentPath, profile.ToString());
        FolderProfile = profile;

        var preferredLayout = profile switch
        {
            DirectoryViewProfile.Photos or DirectoryViewProfile.Videos => LayoutMode.Grid,
            DirectoryViewProfile.Music or DirectoryViewProfile.General or
                DirectoryViewProfile.Desktop or DirectoryViewProfile.Documents or DirectoryViewProfile.Downloads => LayoutMode.Details,
            _ => ResolveLayout(CurrentPath, Items)
        };

        if (preferredLayout == LayoutMode.Grid && Items.Count > GridItemLimit)
            preferredLayout = LayoutMode.Details;

        Layout = preferredLayout;
        if (Layout == LayoutMode.Details)
            _ = EnsureItemIconsAsync();
    }

    /// <summary>
    /// Applies a semantic folder type to every regular folder in the active
    /// multi-selection. Files and drive roots are intentionally ignored.
    /// </summary>
    public void SetFolderProfilesForSelection(DirectoryViewProfile profile)
    {
        var folders = Context.SelectedItems
            .Where(item => item.IsStandardFolder)
            .Select(item => item.FullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (folders.Length == 0)
        {
            StatusText = "Select one or more folders to set their folder type.";
            return;
        }

        SettingsService.SetFolderViewProfiles(folders, profile.ToString());

        // The selected folder tiles are already on screen, so update their
        // lightweight vector mark immediately instead of waiting for a refresh.
        foreach (var item in Context.SelectedItems.Where(item => item.IsStandardFolder))
        {
            item.Thumbnail = null;
            item.GridPlaceholder = null;
            item.Icon = IconService.GetIcon(item);
        }

        var folderLabel = folders.Length == 1 ? "folder" : "folders";
        var typeLabel = profile == DirectoryViewProfile.Automatic ? "automatic" : FolderProfileLabelFor(profile);
        StatusText = $"Set {typeLabel} view for {folders.Length:N0} {folderLabel}.";
    }

    private static string FolderProfileLabelFor(DirectoryViewProfile profile) => profile switch
    {
        DirectoryViewProfile.Desktop => "Desktop",
        DirectoryViewProfile.Documents => "Documents",
        DirectoryViewProfile.Downloads => "Downloads",
        DirectoryViewProfile.General => "General",
        DirectoryViewProfile.Photos => "Photos",
        DirectoryViewProfile.Music => "Music",
        DirectoryViewProfile.Videos => "Videos",
        _ => "Automatic"
    };

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

    private bool _showHiddenItems = SettingsService.GetShowHiddenItems();
    public bool ShowHiddenItems
    {
        get => _showHiddenItems;
        set
        {
            if (!SetProperty(ref _showHiddenItems, value))
                return;

            SettingsService.SetShowHiddenItems(value);
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

        var sorted = _directoryItems.ToList();
        sorted.Sort(new ItemComparer(SortColumn, SortDescending));
        SetDirectoryItems(sorted);
        if (!string.IsNullOrWhiteSpace(CurrentPath))
            FolderSnapshotCache.Set(CurrentPath, sorted);
    }

    /// <summary>
    /// Replaces the directory snapshot while preserving it as the source for
    /// instant search. The visible list may be a smaller filtered projection.
    /// </summary>
    private void SetDirectoryItems(IReadOnlyList<FileSystemItem> items)
    {
        _directoryItems = items;
        ApplySearchFilter(updateStatus: false);
    }

    /// <summary>
    /// Resolves each item's tags from the store. A dictionary lookup per item, so
    /// it is cheap enough to run over a whole listing during load.
    /// </summary>
    private static void ApplyTags(IReadOnlyList<FileSystemItem> items)
    {
        for (var i = 0; i < items.Count; i++)
            items[i].RefreshTags();
    }

    private void ApplySearchFilter(bool updateStatus)
    {
        CancelTreeSearch();

        var query = SearchQuery.Parse(SearchText);

        if (query.IsEmpty)
        {
            _localMatches = [];
            Items = _directoryItems;
            return;
        }

        // Matches in the folder you are standing in appear immediately; the walk of
        // everything beneath it streams in behind them.
        var seed = _directoryItems.Where(query.Matches).ToList();

        // Everywhere is additive, not a replacement. Turning it on should only ever
        // add results: previously it swapped the listing for index hits alone, so a
        // file matching by name here disappeared the moment the query also named a
        // tag, which looked like the toggle losing things.
        if (SearchEverywhere && query.HasIndexFilter)
        {
            var known = new HashSet<string>(seed.Select(item => item.FullPath), StringComparer.OrdinalIgnoreCase);

            foreach (var item in BuildIndexResults(query))
            {
                if (known.Add(item.FullPath))
                    seed.Add(item);
            }
        }

        _localMatches = seed;
        Items = seed.ToArray();

        if (updateStatus)
            UpdateSearchStatus();

        _pendingQuery = query;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    /// <summary>Stops any running subfolder walk and the timer that would start one.</summary>
    private void CancelTreeSearch()
    {
        _searchDebounce.Stop();

        var previous = _searchCancellation;
        _searchCancellation = null;

        if (previous is null)
            return;

        try
        {
            previous.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        previous.Dispose();
        IsSearchingTree = false;
    }

    private bool _isSearchingTree;
    /// <summary>True while subfolders are still being walked.</summary>
    public bool IsSearchingTree
    {
        get => _isSearchingTree;
        private set => SetProperty(ref _isSearchingTree, value);
    }

    /// <summary>
    /// Where a subfolder walk should start.
    ///
    /// Normally just the current folder. With Everywhere on it is every ready local
    /// drive as well, because tags and folder types only know about things you have
    /// labelled: finding a file by name anywhere means actually reading the disks.
    /// The current folder stays first so nearby hits appear before the wider sweep.
    ///
    /// Network drives are deliberately excluded. A disconnected share can block for
    /// tens of seconds per directory and would make every search feel broken.
    /// </summary>
    private IReadOnlyList<string> ResolveSearchRoots()
    {
        var roots = new List<string>();

        if (!string.IsNullOrWhiteSpace(CurrentPath) && Directory.Exists(CurrentPath))
            roots.Add(CurrentPath);

        if (!SearchEverywhere)
            return roots;

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType == DriveType.Network)
                    continue;

                var root = drive.RootDirectory.FullName;

                if (!roots.Any(existing => existing.Equals(root, StringComparison.OrdinalIgnoreCase)))
                    roots.Add(root);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return roots;
    }

    /// <summary>
    /// Walks everything beneath the current folder, reporting hits in batches so
    /// results appear while the walk is still running. A drive root can hold
    /// millions of entries, so this must never block the UI or run to completion
    /// before showing anything.
    /// </summary>
    private async Task RunTreeSearchAsync(SearchQuery query)
    {
        var roots = ResolveSearchRoots();

        if (query.IsEmpty || roots.Count == 0)
            return;

        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var token = cancellation.Token;

        var showHidden = ShowHiddenItems;
        var found = new List<FileSystemItem>(_localMatches);
        var seen = new HashSet<string>(found.Select(item => item.FullPath), StringComparer.OrdinalIgnoreCase);
        var timer = Stopwatch.StartNew();
        var capped = false;

        IsSearchingTree = true;

        var progress = new Progress<IReadOnlyList<FileSystemItem>>(batch =>
        {
            if (token.IsCancellationRequested)
                return;

            foreach (var item in batch)
            {
                if (!seen.Add(item.FullPath))
                    continue;

                // Resolved here rather than on a worker: this runs on the UI thread,
                // so the tag store is only ever read from one thread at a time.
                item.RefreshTags();
                found.Add(item);
            }

            IconService.Populate(batch);
            Items = found.ToArray();
            StatusText = SearchEverywhere
                ? $"Searching all drives… {found.Count:N0} found"
                : $"Searching subfolders… {found.Count:N0} found";
        });

        try
        {
            capped = await Task.Run(
                () => FileSearchService.Run(roots, showHidden, query.Matches, progress, MaxSearchResults, token),
                token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            // A failed walk still leaves the local matches on screen.
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            {
                IsSearchingTree = false;
                _searchCancellation = null;
                cancellation.Dispose();
            }
        }

        if (token.IsCancellationRequested)
            return;

        timer.Stop();

        var scope = SearchEverywhere
            ? "across all drives"
            : "in this folder and subfolders";

        StatusText = found.Count switch
        {
            0 => $"No matches {scope}",
            1 => $"1 match {scope}  ·  {timer.ElapsedMilliseconds} ms",
            _ => capped
                ? $"First {found.Count:N0} matches {scope}  ·  narrow the search to see fewer"
                : $"{found.Count:N0} matches {scope}  ·  {timer.ElapsedMilliseconds} ms"
        };
    }

    /// <summary>
    /// Materialises search hits from the tag and folder-type indexes. Paths that no
    /// longer exist are skipped rather than shown as dead rows.
    /// </summary>
    private static IReadOnlyList<FileSystemItem> BuildIndexResults(SearchQuery query)
    {
        var results = new List<FileSystemItem>();

        foreach (var path in query.IndexCandidates())
        {
            var item = FileSystemItem.FromLocation(path);
            if (item is null)
                continue;

            item.RefreshTags();

            // Re-check the whole query: the index narrowed by tag or type, but any
            // name, extension, or kind terms still have to hold.
            if (!query.Matches(item))
                continue;

            results.Add(item);
        }

        results.Sort(new ItemComparer(SortColumn.Name, descending: false));
        IconService.Populate(results);
        ScalableIconService.PopulateGridPlaceholders(results);
        return results;
    }

    private void UpdateSearchStatus()
    {
        if (!HasSearch)
            return;

        var query = SearchQuery.Parse(SearchText);
        var scope = SearchEverywhere && query.HasIndexFilter
            ? "here and everywhere tagged"
            : "in this folder";
        var description = query.Describe();

        StatusText = Items.Count switch
        {
            0 => description.Length == 0
                ? $"No matches {scope}"
                : $"No matches {scope} for {description}",
            1 => $"1 match {scope}",
            _ => $"{Items.Count:N0} matches {scope}"
        };
    }

    private async Task LoadAsync(string path, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var navigationTimer = Stopwatch.StartNew();
        var keepCurrentItems = force &&
                               path.Equals(CurrentPath, StringComparison.OrdinalIgnoreCase) &&
                               _directoryItems.Count > 0;
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

        // Anything queued for the old folder is now worthless.
        ThumbnailService.CancelPending();
        MediaPropertyService.CancelPending();

        // A subfolder walk belongs to the folder it started from. Left running it
        // would keep streaming hits into the listing for the new location.
        CancelTreeSearch();

        // Navigating away dismisses the photo reel, since it belongs to the folder
        // being left. A refresh of the same folder must not, or saving a rotation
        // would close the viewer you just rotated in. Playback survives either way.
        if (!path.Equals(CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            Viewer.Close();
            // Search is scoped to one directory. Moving to another starts with its
            // full listing rather than leaving behind a confusing old filter.
            if (HasSearch)
                SearchText = string.Empty;
        }

        CurrentPath = path;
        RestoreFolderProfile(path);
        RestoreTileScale(path);

        // Columns are per folder, so they have to be re-read on every navigation,
        // not only when the folder type happens to change.
        LoadColumns();
        AddressText = path;
        IsLoading = true;
        StatusText = hasSnapshot ? "Refreshing cached view…" : "Opening…";
        TimingText = string.Empty;
        ClearHubInfo();

        if (hasSnapshot)
        {
            SetDirectoryItems(snapshot);
            Layout = ResolveLayout(path, snapshot);
            readyMilliseconds = navigationTimer.ElapsedMilliseconds;
            TimingText = $"{readyMilliseconds} ms open · refreshing";
        }
        else if (!keepCurrentItems)
        {
            SetDirectoryItems([]);
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

                SetDirectoryItems(batch);
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
                        ApplyTags(firstBatch);
                        partialProgress.Report(firstBatch);
                        firstBatchReported = true;
                    }
                }

                list.Sort(new ItemComparer(column, descending));
                if (!gridFastPath)
                    IconService.Populate(list);
                ScalableIconService.PopulateGridPlaceholders(list);
                ApplyTags(list);
                return list;
            }, token);

            if (token.IsCancellationRequested)
                return;

            FolderSnapshotCache.Set(path, items);
            SetDirectoryItems(items);
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

            UpdateSearchStatus();

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
            SetDirectoryItems([]);
            StatusText = "You don't have permission to view this folder";
        }
        catch (DirectoryNotFoundException)
        {
            SetDirectoryItems([]);
            StatusText = "That folder no longer exists";
        }
        catch (IOException exception)
        {
            SetDirectoryItems([]);
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
        SetDirectoryItems(drives);
        Layout = LayoutMode.Grid;
        stopwatch.Stop();

        var total = drives.Sum(drive => drive.DriveTotalSpace);
        var available = drives.Sum(drive => drive.DriveAvailableSpace);

        StatusText = networkOnly
            ? drives.Count == 0 ? "No mapped network locations" : $"{drives.Count:N0} network location{(drives.Count == 1 ? string.Empty : "s")}" 
            : $"{drives.Count:N0} drive{(drives.Count == 1 ? string.Empty : "s")}";
        UpdateSearchStatus();
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
        SetDirectoryItems(items);
        Layout = LayoutMode.Grid;
        stopwatch.Stop();
        StatusText = items.Count == 0
            ? isPinnedHub ? "No pinned directories yet" : "No locations available"
            : $"{items.Count:N0} location{(items.Count == 1 ? string.Empty : "s")}";
        UpdateSearchStatus();
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
        var profileName = SettingsService.GetFolderViewProfile(path);
        if (profileName is not null && Enum.TryParse<DirectoryViewProfile>(profileName, out var profile))
        {
            if ((profile is DirectoryViewProfile.Photos or DirectoryViewProfile.Videos) && items.Count <= GridItemLimit)
                return LayoutMode.Grid;
            if (profile is DirectoryViewProfile.General or DirectoryViewProfile.Music or
                DirectoryViewProfile.Desktop or DirectoryViewProfile.Documents or DirectoryViewProfile.Downloads)
                return LayoutMode.Details;
        }

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

    private void RestoreFolderProfile(string path)
    {
        var saved = SettingsService.GetFolderViewProfile(path);
        FolderProfile = saved is not null && Enum.TryParse<DirectoryViewProfile>(saved, out var profile)
            ? profile
            : DirectoryViewProfile.Automatic;
        OnPropertyChanged(nameof(CanSetFolderProfile));
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

/// <summary>A tag row in the context menu, with check state for the selection.</summary>
public sealed class TagOption : ObservableObject
{
    private readonly Action<TagOption> _onToggled;
    private bool _isApplied;

    public TagOption(TagDefinition tag, bool isApplied, Action<TagOption> onToggled)
    {
        Tag = tag;
        _isApplied = isApplied;
        _onToggled = onToggled;
    }

    public TagDefinition Tag { get; }

    public string Name => Tag.Name;

    public bool IsApplied
    {
        get => _isApplied;
        set
        {
            if (SetProperty(ref _isApplied, value))
                _onToggled(this);
        }
    }
}

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
