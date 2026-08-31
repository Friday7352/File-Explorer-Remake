using System.IO;
using Clearspace.Models;

namespace Clearspace.Services;

/// <summary>
/// Everything that has changed on disk since the volumes were walked.
///
/// This is what lets search trust the index without rebuilding it. A walk gives a
/// snapshot; the overlay is the difference between that snapshot and now, and it
/// is small because it only ever holds actual churn - the handful of files
/// created, deleted or renamed while the app has been open.
///
/// The alternative was editing the index in place, which would mean a path-to-entry
/// lookup over millions of paths, which is another dictionary the size of the index
/// itself. Keeping the diff separate costs a set membership check per result and
/// nothing else.
/// </summary>
internal sealed class IndexOverlay
{
    /// <summary>
    /// Above this the overlay has stopped being a small diff, and rebuilding is
    /// cheaper than carrying it. Reaching it drops the index out of trusted mode
    /// rather than letting results quietly drift.
    /// </summary>
    private const int MaxTracked = 100_000;

    /// <summary>
    /// How many deleted subtrees can be carried before rebuilding is cheaper.
    ///
    /// Exact-path removals are a set lookup, but a removed *folder* has to be
    /// tested as a prefix against every result a search returns. A handful of
    /// those is free; thousands would turn each search into a nested loop over
    /// results times deletions, which is precisely the cost this whole design
    /// exists to avoid.
    /// </summary>
    private const int MaxRemovedTrees = 256;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _added = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Deleted folders, kept separately because removing one removes everything
    /// beneath it, and the index still holds all of those descendants.
    /// </summary>
    private readonly List<string> _removedTrees = [];

    /// <summary>True once the overlay has grown past the point of being worth carrying.</summary>
    public bool Overflowed { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
                return _added.Count + _removed.Count;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _added.Clear();
            _removed.Clear();
            _removedTrees.Clear();
            Overflowed = false;
        }
    }

    public void MarkOverflowed()
    {
        lock (_gate)
            Overflowed = true;
    }

    public void OnCreated(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        lock (_gate)
        {
            // A path created again after being deleted is simply present again.
            _removed.Remove(path);
            _added.Add(path);
            CheckSize();
        }
    }

    public void OnDeleted(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        lock (_gate)
        {
            _added.Remove(path);
            _removed.Add(path);

            // The event does not say whether this was a file or a folder - by the
            // time it arrives the thing is gone and cannot be asked. Anything
            // without an extension is treated as a possible subtree, which covers
            // folders while keeping the ordinary case (deleting files, which
            // mostly have extensions) to a set lookup.
            if (!Path.HasExtension(path))
            {
                if (_removedTrees.Count >= MaxRemovedTrees)
                    Overflowed = true;
                else
                    _removedTrees.Add(path);
            }

            CheckSize();
        }
    }

    public void OnRenamed(string oldPath, string newPath)
    {
        OnDeleted(oldPath);
        OnCreated(newPath);
    }

    /// <summary>True when this path has been deleted since the index was built.</summary>
    public bool IsRemoved(string path)
    {
        lock (_gate)
        {
            if (_removed.Count == 0)
                return false;

            if (_removed.Contains(path))
                return true;

            if (_removedTrees.Count == 0)
                return false;

            for (var i = 0; i < _removedTrees.Count; i++)
            {
                var tree = _removedTrees[i];

                if (path.Length > tree.Length &&
                    path.StartsWith(tree, StringComparison.OrdinalIgnoreCase) &&
                    path[tree.Length] == Path.DirectorySeparatorChar)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Items created since the index was built that match the query. These are
    /// stat'd here rather than when the change arrived: events come in bursts, and
    /// most of what they mention is never searched for.
    /// </summary>
    public void CollectMatches(
        IReadOnlyList<string> foldedTerms,
        bool showHidden,
        int limit,
        List<FileSystemItem> results)
    {
        string[] candidates;

        lock (_gate)
        {
            if (_added.Count == 0)
                return;

            candidates = [.. _added];
        }

        foreach (var path in candidates)
        {
            if (results.Count >= limit)
                return;

            var name = Path.GetFileName(path);

            if (name.Length == 0)
                continue;

            var folded = name.ToLowerInvariant();
            var matched = true;

            for (var i = 0; i < foldedTerms.Count; i++)
            {
                if (!folded.Contains(foldedTerms[i], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
                continue;

            var item = FileSystemItem.FromLocation(path);

            if (item is null)
                continue;

            if (!showHidden && (item.IsHidden || (item.Attributes & FileAttributes.System) != 0))
                continue;

            results.Add(item);
        }
    }

    /// <summary>Caller must hold the gate.</summary>
    private void CheckSize()
    {
        if (_added.Count + _removed.Count > MaxTracked)
            Overflowed = true;
    }
}
