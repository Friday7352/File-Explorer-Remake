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
