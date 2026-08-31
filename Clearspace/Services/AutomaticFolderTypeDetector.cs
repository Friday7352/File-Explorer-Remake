using System.IO;
using Clearspace.Models;

namespace Clearspace.Services;

/// <summary>
/// Guesses a semantic folder type from the folder's own name, for folders left on
/// "Automatic" with no explicit type saved.
///
/// This is deliberately name-only. Sniffing contents (majority file extension,
/// say) would mean walking a folder just to label it, which is exactly the kind
/// of extra disk access the rest of Clearspace goes out of its way to avoid.
/// A name check costs nothing and gets the common cases: "Vacation Photos",
/// "Camera Roll", "My Music", "Screenshots".
/// </summary>
internal static class AutomaticFolderTypeDetector
{
    private static readonly string[] PhotoKeywords =
    [
        "photo", "photos", "picture", "pictures", "pics", "image", "images",
        "camera roll", "camera", "screenshot", "screenshots", "wallpaper",
        "wallpapers", "gallery", "snapshots"
    ];

    private static readonly string[] MusicKeywords =
    [
        "music", "musik", "song", "songs", "audio", "album", "albums",
        "soundtrack", "soundtracks", "mp3", "mp3s", "playlist", "playlists",
        "tunes"
    ];

    /// <summary>
    /// The folder type its name suggests, or null when nothing matches and the
    /// folder should keep behaving as General.
    /// </summary>
    public static DirectoryViewProfile? DetectFromName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (ContainsKeyword(name, PhotoKeywords))
            return DirectoryViewProfile.Photos;

        if (ContainsKeyword(name, MusicKeywords))
            return DirectoryViewProfile.Music;

        return null;
    }

    private static bool ContainsKeyword(string name, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
