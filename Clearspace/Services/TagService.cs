using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Clearspace.Services;

/// <summary>A user-visible label that can be attached to any file or folder.</summary>
public sealed record TagDefinition(string Id, string Name, string Color)
{
    private Brush? _brush;

    /// <summary>
    /// The colour as a frozen brush. XAML type conversion only applies to literal
    /// attributes, not to binding results, so binding a hex string straight to a
    /// Background would silently fail; views bind to this instead.
    ///
    /// JsonIgnore is essential. System.Text.Json serialises every public getter, and
    /// walking a Freezable throws, which used to make the whole save fail silently
    /// inside the catch below and lose every tag on exit.
    /// </summary>
    [JsonIgnore]
    public Brush Brush => _brush ??= CreateBrush(Color);

    private static Brush CreateBrush(string color)
    {
        try
        {
            if (ColorConverter.ConvertFromString(color) is Color parsed)
            {
                var brush = new SolidColorBrush(parsed);
                brush.Freeze();
                return brush;
            }
        }
        catch (Exception)
        {
            // Hand-edited tags.json with a malformed colour.
        }

        return Brushes.Gray;
    }
}

internal sealed class TagData
{
    public List<TagDefinition> Definitions { get; set; } = [];

    /// <summary>Full path to the tag ids attached to it.</summary>
    public Dictionary<string, List<string>> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Tag definitions and assignments.
///
/// Kept in its own file rather than settings.json because assignments grow with
/// how much you tag, not with how much you configure: a few thousand tagged files
/// should not mean rewriting the whole config on every toggle.
///
/// The assignment map is also an index. Because it is keyed by path, "every folder
/// tagged Work" is a dictionary scan rather than a disk crawl, which is what makes
/// searching across locations instant.
/// </summary>
public static class TagService
{
    private static readonly string Directory_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Clearspace");

    private static readonly string FilePath = Path.Combine(Directory_, "tags.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static TagData? _data;

    /// <summary>Raised when definitions or assignments change, so views can refresh.</summary>
    public static event EventHandler? Changed;

    private static TagData Current => _data ??= Load();

    public static string TagFilePath => FilePath;

    /// <summary>
    /// Starter set. Broad enough to be useful immediately, small enough that the
    /// menu stays scannable; anything more specific is better as a custom tag.
    /// </summary>
    private static List<TagDefinition> CreateDefaults() =>
    [
        new("important", "Important", "#D3A15F"),
        new("work",      "Work",      "#5B8DD9"),
        new("personal",  "Personal",  "#7FB77E"),
        new("project",   "Project",   "#B07FD9"),
        new("todo",      "To do",     "#D9705B"),
        new("reference", "Reference", "#5BB0C4"),
        new("archive",   "Archive",   "#8A8580")
    ];

    private static TagData Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<TagData>(File.ReadAllText(FilePath), Options);

                if (loaded is not null)
                {
                    loaded.Definitions ??= [];
                    loaded.Assignments = new Dictionary<string, List<string>>(
                        loaded.Assignments ?? [], StringComparer.OrdinalIgnoreCase);

                    if (loaded.Definitions.Count == 0)
                        loaded.Definitions = CreateDefaults();

                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt or unreadable file; start from the defaults rather than fail.
        }

        return new TagData { Definitions = CreateDefaults() };
    }

    private static void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory_);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Options));
        }
        catch (Exception exception)
        {
            // Surfaced rather than swallowed: a save that quietly fails looks exactly
            // like tags not persisting, which is far harder to diagnose than an error.
            System.Diagnostics.Trace.WriteLine($"Clearspace: could not save tags. {exception}");
            LastSaveError = exception.Message;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Set when the last write failed, so the view model can report it.</summary>
    public static string? LastSaveError { get; private set; }

    // ---------- Definitions ----------

    public static IReadOnlyList<TagDefinition> All => Current.Definitions;

    public static TagDefinition? Find(string id)
        => Current.Definitions.FirstOrDefault(tag => tag.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves by id first, then by display name, so search accepts either.</summary>
    public static TagDefinition? Resolve(string idOrName)
        => Find(idOrName) ?? Current.Definitions.FirstOrDefault(
            tag => tag.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    public static TagDefinition Create(string name)
    {
        var trimmed = name.Trim();
        var existing = Resolve(trimmed);
        if (existing is not null)
            return existing;

        var id = MakeId(trimmed);
        var tag = new TagDefinition(id, trimmed, NextColor());

        Current.Definitions.Add(tag);
        Save();
        return tag;
    }

    public static void Rename(string id, string name)
    {
        var index = Current.Definitions.FindIndex(tag => tag.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;

        Current.Definitions[index] = Current.Definitions[index] with { Name = name.Trim() };
        Save();
    }

    public static void Delete(string id)
    {
        if (Current.Definitions.RemoveAll(tag => tag.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) == 0)
            return;

        // Strip the tag from everything holding it, or the assignment map would
        // accumulate ids that no longer resolve to anything.
        foreach (var path in Current.Assignments.Keys.ToList())
        {
            var ids = Current.Assignments[path];
            if (ids.RemoveAll(value => value.Equals(id, StringComparison.OrdinalIgnoreCase)) > 0 && ids.Count == 0)
                Current.Assignments.Remove(path);
        }

        Save();
    }

    private static string MakeId(string name)
    {
        var stem = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (string.IsNullOrEmpty(stem))
            stem = "tag";

        var candidate = stem;
        var suffix = 2;
        while (Find(candidate) is not null)
            candidate = $"{stem}{suffix++}";

        return candidate;
    }

    private static readonly string[] Palette =
    [
        "#D3A15F", "#5B8DD9", "#7FB77E", "#B07FD9",
        "#D9705B", "#5BB0C4", "#C4A85B", "#C45B93"
    ];

    private static string NextColor() => Palette[Current.Definitions.Count % Palette.Length];

    // ---------- Assignments ----------

    public static IReadOnlyList<string> TagIdsFor(string path)
        => Current.Assignments.TryGetValue(path, out var ids) ? ids : [];

    public static IReadOnlyList<TagDefinition> TagsFor(string path)
    {
        var ids = TagIdsFor(path);
        if (ids.Count == 0)
            return [];

        return ids.Select(Find).Where(tag => tag is not null).Cast<TagDefinition>().ToArray();
    }

    public static bool HasTag(string path, string tagId)
        => TagIdsFor(path).Any(id => id.Equals(tagId, StringComparison.OrdinalIgnoreCase));

    public static void Assign(string path, string tagId)
    {
        if (Find(tagId) is null || HasTag(path, tagId))
            return;

        if (!Current.Assignments.TryGetValue(path, out var ids))
            Current.Assignments[path] = ids = [];

        ids.Add(tagId);
        Save();
    }

    public static void Unassign(string path, string tagId)
    {
        if (!Current.Assignments.TryGetValue(path, out var ids))
            return;

        if (ids.RemoveAll(id => id.Equals(tagId, StringComparison.OrdinalIgnoreCase)) == 0)
            return;

        if (ids.Count == 0)
            Current.Assignments.Remove(path);

        Save();
    }

    /// <summary>
    /// Applies or removes a tag across a selection in one write. Adds to all when
    /// any lacks it, which matches how a checkbox over a mixed selection behaves.
    /// </summary>
    public static void ToggleForAll(IReadOnlyList<string> paths, string tagId)
    {
        if (paths.Count == 0 || Find(tagId) is null)
            return;

        var everyoneHasIt = paths.All(path => HasTag(path, tagId));

        foreach (var path in paths)
        {
            if (everyoneHasIt)
            {
                if (Current.Assignments.TryGetValue(path, out var ids))
                {
                    ids.RemoveAll(id => id.Equals(tagId, StringComparison.OrdinalIgnoreCase));
                    if (ids.Count == 0)
                        Current.Assignments.Remove(path);
                }
            }
            else if (!HasTag(path, tagId))
            {
                if (!Current.Assignments.TryGetValue(path, out var ids))
                    Current.Assignments[path] = ids = [];

                ids.Add(tagId);
            }
        }

        Save();
    }

    public static void ClearTags(IReadOnlyList<string> paths)
    {
        var changed = false;

        foreach (var path in paths)
            changed |= Current.Assignments.Remove(path);

        if (changed)
            Save();
    }

    /// <summary>Every tagged path. This is the index searches across locations read.</summary>
    public static IEnumerable<KeyValuePair<string, List<string>>> Assignments => Current.Assignments;

    public static IEnumerable<string> PathsWithTag(string tagId)
        => Current.Assignments
            .Where(pair => pair.Value.Any(id => id.Equals(tagId, StringComparison.OrdinalIgnoreCase)))
            .Select(pair => pair.Key);

    /// <summary>Forgets assignments whose file or folder no longer exists.</summary>
    public static int PruneMissing()
    {
        var gone = Current.Assignments.Keys
            .Where(path => !File.Exists(path) && !System.IO.Directory.Exists(path))
            .ToList();

        foreach (var path in gone)
            Current.Assignments.Remove(path);

        if (gone.Count > 0)
            Save();

        return gone.Count;
    }
}
