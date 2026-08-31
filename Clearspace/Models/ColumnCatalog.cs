using System.Collections.ObjectModel;

namespace Clearspace.Models;

/// <summary>
/// One selectable column. <see cref="ResourceKey"/> names a GridViewColumn declared
/// in MainWindow's resources, which is where the cell templates and their event
/// handlers live.
/// </summary>
public sealed record ColumnInfo(string Id, string Header, string ResourceKey, bool IsRequired = false);

/// <summary>
/// The set of columns Clearspace can show in details view, and which ones each
/// folder type starts with.
/// </summary>
public static class ColumnCatalog
{
    public static readonly IReadOnlyList<ColumnInfo> All =
    [
        // The name column carries the per-row metadata request, so it cannot be
        // switched off without the music columns going permanently blank.
        new("name",         "Name",          "Col.Name",         IsRequired: true),
        new("play",         "Play",          "Col.Play"),
        new("track",        "Track number",  "Col.Track"),
        new("title",        "Title",         "Col.Title"),
        new("artist",       "Artist",        "Col.Artist"),
        new("album",        "Album",         "Col.Album"),
        new("length",       "Length",        "Col.Length"),
        new("datemodified", "Date modified", "Col.DateModified"),
        new("datecreated",  "Date created",  "Col.DateCreated"),
        new("tags",         "Tags",          "Col.Tags"),
        new("status",       "Status",        "Col.Status"),
        new("type",         "Type",          "Col.Type"),
        new("size",         "Size",          "Col.Size")
    ];

    // Name, date modified, type. Size is off by default: it is blank for every
    // folder row, so it earns its place only when you ask for it.
    private static readonly string[] GeneralDefault = ["name", "datemodified", "type"];
    private static readonly string[] PhotosDefault = ["name", "datemodified", "type"];
    private static readonly string[] MusicDefault = ["play", "name", "artist", "album", "length", "datemodified"];

    // Status earns its place only inside a sync root. Everywhere else the column
    // would be a permanently blank strip, which is why it is not in the general
    // default and is added by DefaultsFor instead.
    private static readonly string[] CloudDefault = ["name", "status", "datemodified", "type"];

    public static IReadOnlyList<string> DefaultsFor(DirectoryViewProfile profile) => profile switch
    {
        DirectoryViewProfile.Music => MusicDefault,
        DirectoryViewProfile.Photos => PhotosDefault,
        _ => GeneralDefault
    };

    /// <summary>
    /// The starting columns for a folder, given whether it is inside a cloud
    /// provider's sync root.
    /// </summary>
    public static IReadOnlyList<string> DefaultsFor(DirectoryViewProfile profile, bool isCloudFolder)
        => isCloudFolder && profile is DirectoryViewProfile.General or DirectoryViewProfile.Automatic
            ? CloudDefault
            : DefaultsFor(profile);

    public static ColumnInfo? Find(string id)
        => All.FirstOrDefault(column => column.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Filters a saved list down to ids that still exist, keeping order.</summary>
    public static List<string> Sanitise(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var id in ids)
        {
            if (Find(id) is null || !seen.Add(id))
                continue;

            result.Add(id);
        }

        // The required column is re-inserted at the front rather than rejected, so a
        // hand-edited settings file can never produce a list with no names in it.
        foreach (var required in All.Where(column => column.IsRequired))
        {
            if (!seen.Contains(required.Id))
                result.Insert(0, required.Id);
        }

        return result;
    }
}

/// <summary>A row in the column picker menu.</summary>
public sealed class ColumnOption : ObservableObject
{
    private readonly Action<ColumnOption> _onToggled;
    private bool _isVisible;

    public ColumnOption(ColumnInfo info, bool isVisible, Action<ColumnOption> onToggled)
    {
        Info = info;
        _isVisible = isVisible;
        _onToggled = onToggled;
    }

    public ColumnInfo Info { get; }

    public string Id => Info.Id;

    public string Header => Info.Header;

    /// <summary>The required column stays checked and is greyed out in the menu.</summary>
    public bool CanToggle => !Info.IsRequired;

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (Info.IsRequired)
                return;

            if (SetProperty(ref _isVisible, value))
                _onToggled(this);
        }
    }
}
