using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Clearspace.Services;

public sealed class SettingsData
{
    /// <summary>Folder path to layout name. Explorer calls this folder type discovery.</summary>
    public Dictionary<string, string> FolderLayouts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Grid zoom is a per-location preference, separate from the chosen view.</summary>
    public Dictionary<string, double> FolderTileScales { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folder path to its semantic view profile: General, Photos, or Music.</summary>
    public Dictionary<string, string> FolderViewProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the Windows Search index is consulted for instant results. The
    /// filesystem crawl runs either way, so turning this off costs speed but never
    /// completeness.
    /// </summary>
    public bool UseWindowsIndex { get; set; } = true;

    /// <summary>
    /// Whether hidden and system items are listed. Global rather than per folder,
    /// matching how Explorer treats it: it is a statement about how you want to
    /// work, not about one location.
    /// </summary>
    public bool ShowHiddenItems { get; set; }

    /// <summary>
    /// Whether a tag or folder-type query is answered from the saved indexes rather
    /// than the current listing. Global for the same reason as the two above: it
    /// describes how you want to search, not where you happen to be standing.
    /// </summary>
    public bool SearchEverywhere { get; set; }

    /// <summary>
    /// Whether searching also looks inside documents, or matches names only.
    ///
    /// Separate from the index settings because it is a different question. Names
    /// are answered from Clearspace's own index in memory; contents can only come
    /// from the Windows index, which has run filters over the files themselves.
    /// Wanting one is not wanting the other - "find the file called invoice" and
    /// "find the file that mentions invoice" are different searches.
    /// </summary>
    public bool SearchFileContents { get; set; } = true;

    /// <summary>
    /// Chosen detail columns per folder. Explorer works this way too: which columns
    /// are useful is a property of what is in this particular folder, not of the
    /// whole machine.
    /// </summary>
    public Dictionary<string, List<string>> FolderColumns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resized details columns per folder. Width is deliberately separate from the
    /// selected-column list so an older settings file stays compatible and hiding a
    /// column does not make it forget the width you gave it.
    /// </summary>
    public Dictionary<string, Dictionary<string, double>> FolderColumnWidths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Chosen detail columns per view profile, not per folder. Columns describe the
    /// kind of content, so setting them once for Music applies to every music
    /// folder instead of needing to be redone in each one.
    /// </summary>
    public Dictionary<string, List<string>> ProfileColumns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Sidebar entry name to a user-chosen path, overriding the known folder.</summary>
    public Dictionary<string, string> SidebarOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directory path to its display name in the persistent Pinned directories section.</summary>
    public Dictionary<string, string> PinnedDirectories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<PinnedCategory> PinnedCategories { get; set; } = [];

    /// <summary>Path to its optional category identifier. Missing means ungrouped.</summary>
    public Dictionary<string, string> PinnedDirectoryCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stable pin order, independent of the display name.</summary>
    public List<string> PinnedDirectoryOrder { get; set; } = [];

    public List<SidebarSectionConfig> SidebarSections { get; set; } = [];

    public List<string> SidebarSectionOrder { get; set; } = [];
}

public sealed class PinnedCategory
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New category";
    public bool IsCollapsed { get; set; }
}

public sealed class SidebarSectionConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsCollapsed { get; set; }
}

public sealed record SidebarSectionInfo(string Id, string Name, bool IsCollapsed, bool IsCategory);

/// <summary>
/// Settings live in a plain JSON file the user can read and edit by hand.
/// Writes are best-effort: a settings failure must never interrupt browsing.
/// </summary>
public static class SettingsService
{
    private static readonly string Directory_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Clearspace");

    private static readonly string FilePath = Path.Combine(Directory_, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static SettingsData? _data;

    public static SettingsData Current => _data ??= Load();

    public static string SettingsFilePath => FilePath;

    private static SettingsData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<SettingsData>(json, Options);

                if (loaded is not null)
                {
                    // Deserialised dictionaries lose the comparer, so rebuild them.
                    loaded.FolderLayouts = new Dictionary<string, string>(loaded.FolderLayouts, StringComparer.OrdinalIgnoreCase);
                    loaded.FolderTileScales = new Dictionary<string, double>(loaded.FolderTileScales ?? [], StringComparer.OrdinalIgnoreCase);
                    loaded.FolderViewProfiles = new Dictionary<string, string>(loaded.FolderViewProfiles ?? [], StringComparer.OrdinalIgnoreCase);
                    loaded.ProfileColumns = new Dictionary<string, List<string>>(loaded.ProfileColumns ?? [], StringComparer.OrdinalIgnoreCase);
                    loaded.FolderColumns = new Dictionary<string, List<string>>(loaded.FolderColumns ?? [], StringComparer.OrdinalIgnoreCase);
                    loaded.FolderColumnWidths = new Dictionary<string, Dictionary<string, double>>(
                        (loaded.FolderColumnWidths ?? [])
                            .Select(pair => new KeyValuePair<string, Dictionary<string, double>>(
                                pair.Key,
                                new Dictionary<string, double>(pair.Value ?? [], StringComparer.OrdinalIgnoreCase))),
                        StringComparer.OrdinalIgnoreCase);
                    loaded.SidebarOverrides = new Dictionary<string, string>(loaded.SidebarOverrides, StringComparer.OrdinalIgnoreCase);
                    loaded.PinnedDirectories = new Dictionary<string, string>(loaded.PinnedDirectories, StringComparer.OrdinalIgnoreCase);
                    loaded.PinnedCategories ??= [];
                    loaded.PinnedDirectoryCategories = new Dictionary<string, string>(loaded.PinnedDirectoryCategories ?? [], StringComparer.OrdinalIgnoreCase);
                    loaded.PinnedDirectoryOrder ??= [];
                    loaded.SidebarSections ??= [];
                    loaded.SidebarSectionOrder ??= [];
                    foreach (var path in loaded.PinnedDirectories.Keys)
                    {
                        if (!loaded.PinnedDirectoryOrder.Contains(path, StringComparer.OrdinalIgnoreCase))
                            loaded.PinnedDirectoryOrder.Add(path);
                    }
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt or unreadable file; start clean rather than refusing to launch.
        }

        return new SettingsData();
    }

    public static void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory_);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
        }
        catch (Exception)
        {
            // Read-only profile or a locked file. Settings stay in memory for this session.
        }
    }

    public static string? GetFolderLayout(string folder)
        => Current.FolderLayouts.TryGetValue(folder, out var layout) ? layout : null;

    public static void SetFolderLayout(string folder, string layout)
    {
        Current.FolderLayouts[folder] = layout;
        Save();
    }

    public static double? GetFolderTileScale(string folder)
        => Current.FolderTileScales.TryGetValue(folder, out var scale) ? scale : null;

    public static void SetFolderTileScale(string folder, double scale)
    {
        Current.FolderTileScales[folder] = scale;
        Save();
    }

    public static string? GetFolderViewProfile(string folder)
        => Current.FolderViewProfiles.TryGetValue(folder, out var profile) ? profile : null;

    public static void SetFolderViewProfile(string folder, string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile) ||
            profile.Equals("Automatic", StringComparison.OrdinalIgnoreCase))
            Current.FolderViewProfiles.Remove(folder);
        else
            Current.FolderViewProfiles[folder] = profile;

        Save();
    }

    /// <summary>
    /// Applies one folder type to several folders and writes settings only once.
    /// This keeps a multi-select action responsive even for a large selection.
    /// </summary>
    public static void SetFolderViewProfiles(IEnumerable<string> folders, string? profile)
    {
        var applyAutomatic = string.IsNullOrWhiteSpace(profile) ||
            profile.Equals("Automatic", StringComparison.OrdinalIgnoreCase);

        foreach (var folder in folders
                     .Where(folder => !string.IsNullOrWhiteSpace(folder))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (applyAutomatic)
                Current.FolderViewProfiles.Remove(folder);
            else
                Current.FolderViewProfiles[folder] = profile!;
        }

        Save();
    }

    public static bool GetUseWindowsIndex() => Current.UseWindowsIndex;

    public static void SetUseWindowsIndex(bool value)
    {
        if (Current.UseWindowsIndex == value)
            return;

        Current.UseWindowsIndex = value;
        Save();
    }

    public static bool GetShowHiddenItems() => Current.ShowHiddenItems;

    public static void SetShowHiddenItems(bool value)
    {
        if (Current.ShowHiddenItems == value)
            return;

        Current.ShowHiddenItems = value;
        Save();
    }

    public static bool GetSearchFileContents() => Current.SearchFileContents;

    public static void SetSearchFileContents(bool value)
    {
        if (Current.SearchFileContents == value)
            return;

        Current.SearchFileContents = value;
        Save();
    }

    public static bool GetSearchEverywhere() => Current.SearchEverywhere;

    public static void SetSearchEverywhere(bool value)
    {
        if (Current.SearchEverywhere == value)
            return;

        Current.SearchEverywhere = value;
        Save();
    }

    /// <summary>
    /// Every folder that has been given an explicit type, path to profile name.
    /// Searching by folder type reads this rather than walking the disk.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetAllFolderViewProfiles() => Current.FolderViewProfiles;

    /// <summary>Saved column ids for one folder, or null when it has never been set.</summary>
    public static IReadOnlyList<string>? GetFolderColumns(string folder)
        => Current.FolderColumns.TryGetValue(folder, out var columns) && columns.Count > 0
            ? columns
            : null;

    public static void SetFolderColumns(string folder, IEnumerable<string> columnIds)
    {
        Current.FolderColumns[folder] = columnIds.ToList();
        Save();
    }

    public static void ClearFolderColumns(string folder)
    {
        var changed = Current.FolderColumns.Remove(folder);
        changed |= Current.FolderColumnWidths.Remove(folder);
        if (changed)
            Save();
    }

    /// <summary>Saved width for one details column in one folder.</summary>
    public static double? GetFolderColumnWidth(string folder, string columnId)
        => Current.FolderColumnWidths.TryGetValue(folder, out var widths) &&
           widths.TryGetValue(columnId, out var width) &&
           !double.IsNaN(width) && !double.IsInfinity(width) && width > 0
            ? width
            : null;

    /// <summary>
    /// Records the finished size of a user-resized details column. The caller only
    /// invokes this at the end of a drag, rather than writing settings for every
    /// pixel the resize thumb moves through.
    /// </summary>
    public static void SetFolderColumnWidth(string folder, string columnId, double width)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(columnId) ||
            double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            return;

        if (!Current.FolderColumnWidths.TryGetValue(folder, out var widths))
        {
            widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Current.FolderColumnWidths[folder] = widths;
        }

        if (widths.TryGetValue(columnId, out var saved) && Math.Abs(saved - width) < .5)
            return;

        widths[columnId] = width;
        Save();
    }

    /// <summary>Saved column ids for a profile, or null when it has never been set.</summary>
    public static IReadOnlyList<string>? GetProfileColumns(string profile)
        => Current.ProfileColumns.TryGetValue(profile, out var columns) && columns.Count > 0
            ? columns
            : null;

    public static void SetProfileColumns(string profile, IEnumerable<string> columnIds)
    {
        Current.ProfileColumns[profile] = columnIds.ToList();
        Save();
    }

    public static string? GetSidebarOverride(string name)
        => Current.SidebarOverrides.TryGetValue(name, out var path) ? path : null;

    public static void SetSidebarOverride(string name, string path)
    {
        Current.SidebarOverrides[name] = path;
        Save();
    }

    public static void ClearSidebarOverride(string name)
    {
        if (Current.SidebarOverrides.Remove(name))
            Save();
    }

    public static IReadOnlyDictionary<string, string> GetPinnedDirectories() => Current.PinnedDirectories;

    public static void PinDirectory(string path, string name)
    {
        Current.PinnedDirectories[path] = name;
        if (!Current.PinnedDirectoryOrder.Contains(path, StringComparer.OrdinalIgnoreCase))
            Current.PinnedDirectoryOrder.Add(path);
        Save();
    }

    public static void UnpinDirectory(string path)
    {
        if (Current.PinnedDirectories.Remove(path))
        {
            Current.PinnedDirectoryCategories.Remove(path);
            Current.PinnedDirectoryOrder.RemoveAll(item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    public static IReadOnlyList<PinnedCategory> GetPinnedCategories() => Current.PinnedCategories;

    public static IReadOnlyList<KeyValuePair<string, string>> GetPins(string? categoryId)
    {
        var order = Current.PinnedDirectoryOrder
            .Select((path, index) => new { path, index })
            .ToDictionary(item => item.path, item => item.index, StringComparer.OrdinalIgnoreCase);

        return Current.PinnedDirectories
            .Where(pin => Current.PinnedDirectoryCategories.TryGetValue(pin.Key, out var assigned)
                ? string.Equals(assigned, categoryId, StringComparison.OrdinalIgnoreCase)
                : categoryId is null)
            .OrderBy(pin => order.TryGetValue(pin.Key, out var index) ? index : int.MaxValue)
            .ToList();
    }

    public static void CreatePinnedCategory(string name)
    {
        EnsureSidebarSections();
        var category = new PinnedCategory { Name = name };
        Current.PinnedCategories.Add(category);
        var favoritesIndex = Current.SidebarSectionOrder.FindIndex(id => id == "favorites");
        Current.SidebarSectionOrder.Insert(favoritesIndex < 0 ? 0 : favoritesIndex + 1, CategorySectionId(category.Id));
        Save();
    }

    public static void RenamePinnedCategory(string id, string name)
    {
        var category = Current.PinnedCategories.FirstOrDefault(item => item.Id == id);
        if (category is null) return;
        category.Name = name;
        Save();
    }

    public static void DeletePinnedCategory(string id)
    {
        if (Current.PinnedCategories.RemoveAll(item => item.Id == id) == 0)
            return;

        foreach (var path in Current.PinnedDirectoryCategories
                     .Where(pair => pair.Value == id)
                     .Select(pair => pair.Key)
                     .ToList())
            Current.PinnedDirectoryCategories.Remove(path);

        Current.SidebarSectionOrder.RemoveAll(section => section == CategorySectionId(id));

        Save();
    }

    public static void TogglePinnedCategory(string id)
    {
        var category = Current.PinnedCategories.FirstOrDefault(item => item.Id == id);
        if (category is null) return;
        category.IsCollapsed = !category.IsCollapsed;
        Save();
    }

    public static void MovePinnedDirectory(string path, string? categoryId, string? targetPath = null, bool placeAfter = false)
    {
        if (!Current.PinnedDirectories.ContainsKey(path)) return;

        if (string.IsNullOrEmpty(categoryId))
            Current.PinnedDirectoryCategories.Remove(path);
        else
            Current.PinnedDirectoryCategories[path] = categoryId;

        Current.PinnedDirectoryOrder.RemoveAll(item => item.Equals(path, StringComparison.OrdinalIgnoreCase));
        var targetIndex = string.IsNullOrWhiteSpace(targetPath)
            ? -1
            : Current.PinnedDirectoryOrder.FindIndex(item => item.Equals(targetPath, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0)
            Current.PinnedDirectoryOrder.Add(path);
        else
            Current.PinnedDirectoryOrder.Insert(targetIndex + (placeAfter ? 1 : 0), path);

        Save();
    }

    public static void MovePinnedCategory(string sourceId, string beforeId)
    {
        var source = Current.PinnedCategories.FirstOrDefault(item => item.Id == sourceId);
        var targetIndex = Current.PinnedCategories.FindIndex(item => item.Id == beforeId);
        if (source is null || targetIndex < 0 || sourceId == beforeId) return;

        Current.PinnedCategories.Remove(source);
        targetIndex = Current.PinnedCategories.FindIndex(item => item.Id == beforeId);
        Current.PinnedCategories.Insert(targetIndex, source);
        Save();
    }

    public static IReadOnlyList<SidebarSectionInfo> GetSidebarSections()
    {
        EnsureSidebarSections();

        return Current.SidebarSectionOrder
            .Select(ToSectionInfo)
            .Where(section => section is not null)
            .Cast<SidebarSectionInfo>()
            .ToList();
    }

    public static void ToggleSidebarSection(string sectionId)
    {
        EnsureSidebarSections();
        if (TryGetCategoryId(sectionId, out var categoryId))
        {
            TogglePinnedCategory(categoryId);
            return;
        }

        var section = Current.SidebarSections.FirstOrDefault(item => item.Id == sectionId);
        if (section is null) return;
        section.IsCollapsed = !section.IsCollapsed;
        Save();
    }

    public static void RenameSidebarSection(string sectionId, string name)
    {
        EnsureSidebarSections();
        if (TryGetCategoryId(sectionId, out var categoryId))
        {
            RenamePinnedCategory(categoryId, name);
            return;
        }

        var section = Current.SidebarSections.FirstOrDefault(item => item.Id == sectionId);
        if (section is null) return;
        section.Name = name;
        Save();
    }

    public static void MoveSidebarSection(string sourceId, string targetId, bool placeAfter)
    {
        EnsureSidebarSections();
        if (sourceId == targetId || !Current.SidebarSectionOrder.Remove(sourceId)) return;
        var targetIndex = Current.SidebarSectionOrder.IndexOf(targetId);
        if (targetIndex < 0)
            Current.SidebarSectionOrder.Add(sourceId);
        else
            Current.SidebarSectionOrder.Insert(targetIndex + (placeAfter ? 1 : 0), sourceId);
        Save();
    }

    private static readonly (string Id, string Name)[] DefaultSidebarSections =
    [
        ("files", "Your files"),
        ("favorites", "Favorites"),
        // Rendered only when a provider is actually signed in, so a machine with
        // no OneDrive never sees an empty heading.
        ("cloud", "Cloud"),
        ("this-pc", "This PC"),
        ("network", "Network")
    ];

    private static void EnsureSidebarSections()
    {
        foreach (var (id, name) in DefaultSidebarSections)
        {
            if (Current.SidebarSections.All(section => section.Id != id))
                Current.SidebarSections.Add(new SidebarSectionConfig { Id = id, Name = name });
        }

        var known = DefaultSidebarSections.Select(item => item.Id)
            .Concat(Current.PinnedCategories.Select(category => CategorySectionId(category.Id)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Current.SidebarSectionOrder.RemoveAll(id => !known.Contains(id));

        // A previously saved category predates the draggable sidebar. Keep it near
        // Favorites on first launch rather than losing it or placing it at the end.
        foreach (var category in Current.PinnedCategories)
        {
            var categoryId = CategorySectionId(category.Id);
            if (Current.SidebarSectionOrder.Contains(categoryId)) continue;
            var favoritesIndex = Current.SidebarSectionOrder.FindIndex(id => id == "favorites");
            Current.SidebarSectionOrder.Insert(favoritesIndex < 0 ? Current.SidebarSectionOrder.Count : favoritesIndex + 1, categoryId);
        }

        foreach (var (id, _) in DefaultSidebarSections)
        {
            if (!Current.SidebarSectionOrder.Contains(id))
                Current.SidebarSectionOrder.Add(id);
        }
    }

    private static SidebarSectionInfo? ToSectionInfo(string id)
    {
        if (TryGetCategoryId(id, out var categoryId))
        {
            var category = Current.PinnedCategories.FirstOrDefault(item => item.Id == categoryId);
            return category is null ? null : new SidebarSectionInfo(id, category.Name, category.IsCollapsed, IsCategory: true);
        }

        var section = Current.SidebarSections.FirstOrDefault(item => item.Id == id);
        return section is null ? null : new SidebarSectionInfo(id, section.Name, section.IsCollapsed, IsCategory: false);
    }

    private static string CategorySectionId(string categoryId) => $"category:{categoryId}";

    private static bool TryGetCategoryId(string sectionId, out string categoryId)
    {
        const string prefix = "category:";
        if (sectionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            categoryId = sectionId[prefix.Length..];
            return true;
        }

        categoryId = string.Empty;
        return false;
    }
}
