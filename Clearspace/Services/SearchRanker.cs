using System.IO;
using Clearspace.Models;
using Clearspace.Native;

namespace Clearspace.Services;

/// <summary>
/// Puts the results you meant at the top.
///
/// A name index can answer "which files contain this text" in milliseconds, but
/// it has no opinion about which of four thousand answers you wanted, and walk
/// order is not an opinion. Searching "backrooms" turning up a sixty-character
/// cache blob above a folder actually called "The Backrooms" is a correct result
/// and a useless one.
///
/// Everything leaves this to sorting by name or date, which works because its
/// users learn its query syntax. The bet here is the opposite one: that a good
/// default order is worth more than a language to specify order in.
/// </summary>
internal static class SearchRanker
{
    // Match quality. These dominate every other signal, because how well the name
    // matches is the question and the rest is tie-breaking.
    private const int ExactStem = 1000;
    private const int StartsWith = 600;
    private const int WordStart = 400;
    private const int Anywhere = 150;

    /// <summary>
    /// Paths whose contents are almost never what someone is looking for. Machine
    /// output, caches, and the parts of Windows that belong to Windows.
    /// </summary>
    private static readonly string[] HardNoise =
    [
        @"\appdata\", @"\node_modules\", @"\.git\", @"\temp\", @"\tmp\",
        @"\windows\", @"\programdata\", @"\$recycle.bin\", @"\cache\",
        @"\caches\", @"\.vs\", @"\package cache\", @"\system volume information\"
    ];

    /// <summary>
    /// Build output and package stores. Demoted, not buried: sometimes the file in
    /// bin is exactly the one you want.
    /// </summary>
    private static readonly string[] SoftNoise =
    [
        @"\obj\", @"\bin\", @"\.nuget\", @"\.gradle\", @"\node\", @"\dist\"
    ];

    /// <summary>Extensions that are usually a by-product of something else.</summary>
    private static readonly HashSet<string> DerivedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".log", ".bak", ".cache", ".pdb", ".obj", ".idb", ".ilk",
        ".dmp", ".etl", ".old", ".part", ".crdownload"
    };

    /// <summary>
    /// Orders results in place, best first.
    ///
    /// Scores are computed once per item and sorted alongside them rather than
    /// recomputed inside the comparison: a comparison sort asks O(n log n) times,
    /// and scoring does real string work.
    /// </summary>
    public static void Rank(
        List<FileSystemItem> items,
        IReadOnlyList<string> terms,
        string? currentFolder)
    {
        if (items.Count < 2 || terms.Count == 0)
            return;

        var scored = new (int Score, FileSystemItem Item)[items.Count];

        for (var i = 0; i < items.Count; i++)
            scored[i] = (Score(items[i], terms, currentFolder), items[i]);

        Array.Sort(scored, (left, right) =>
        {
            // Descending: the best score first.
            var byScore = right.Score.CompareTo(left.Score);

            return byScore != 0 ? byScore : CompareTies(left.Item, right.Item);
        });

        items.Clear();

        foreach (var entry in scored)
            items.Add(entry.Item);
    }

    public static int Score(FileSystemItem item, IReadOnlyList<string> terms, string? currentFolder)
    {
        var name = item.Name;

        if (name.Length == 0)
            return 0;

        var quality = 0;
        var matchedLength = 0;
        var stem = Path.GetFileNameWithoutExtension(name);

        foreach (var term in terms)
        {
            if (term.Length == 0)
                continue;

            var at = name.IndexOf(term, StringComparison.OrdinalIgnoreCase);

            if (at < 0)
                continue;

            matchedLength += term.Length;

            quality += stem.Equals(term, StringComparison.OrdinalIgnoreCase)
                ? ExactStem
                : at == 0
                    ? StartsWith
                    : IsWordStart(name, at)
                        ? WordStart
                        : Anywhere;
        }

        if (quality == 0)
            return 0;

        // Averaged, so a two-word query is not worth twice a one-word query.
        var score = quality / terms.Count;

        // How much of the name the match accounts for. "The Backrooms" is mostly
        // the thing you searched for; a sixty-character cache blob that happens to
        // contain it is mostly something else.
        score += (int)(220.0 * matchedLength / name.Length);

        // Folders are usually navigational - finding one answers the question of
        // where the rest of it lives.
        if (item.IsFolder)
            score += 90;

        var path = item.FullPath;

        // Something in the folder you are standing in is far more likely to be
        // what you meant than the same name six drives away.
        if (!string.IsNullOrEmpty(currentFolder) &&
            path.StartsWith(currentFolder, StringComparison.OrdinalIgnoreCase))
        {
            score += 260;
        }

        foreach (var segment in HardNoise)
        {
            if (path.Contains(segment, StringComparison.OrdinalIgnoreCase))
            {
                score -= 500;
                break;
            }
        }

        foreach (var segment in SoftNoise)
        {
            if (path.Contains(segment, StringComparison.OrdinalIgnoreCase))
            {
                score -= 150;
                break;
            }
        }

        if (!item.IsFolder)
        {
            if (DerivedTypes.Contains(item.Extension))
                score -= 220;

            // A shortcut is a pointer at the thing, not the thing.
            if (item.IsShortcut)
                score -= 70;
        }

        // Recently touched files are more often the ones being looked for, but
        // only as a tie-breaker - an old file with a perfect name still wins.
        var age = DateTime.Now - item.DateModified;

        if (item.DateModified != DateTime.MinValue)
        {
            score += age.TotalDays switch
            {
                < 7 => 70,
                < 30 => 45,
                < 365 => 20,
                _ => 0
            };
        }

        return score;
    }

    /// <summary>
    /// Whether the match begins a word rather than landing inside one.
    ///
    /// Both separators and case changes count, so "backrooms" is a word start in
    /// "The Backrooms" and in "TheBackroomsPortal", but not in "thebackroomsblob".
    /// </summary>
    private static bool IsWordStart(string name, int at)
    {
        if (at <= 0)
            return true;

        var previous = name[at - 1];

        if (!char.IsLetterOrDigit(previous))
            return true;

        return char.IsUpper(name[at]) && !char.IsUpper(previous);
    }

    /// <summary>
    /// Folders before files, then Explorer's natural name order. Used to break
    /// ties between results the score cannot separate.
    /// </summary>
    public static int CompareTies(FileSystemItem x, FileSystemItem y)
    {
        if (x.IsFolder != y.IsFolder)
            return x.IsFolder ? -1 : 1;

        return NativeMethods.StrCmpLogicalW(x.Name, y.Name);
    }
}
