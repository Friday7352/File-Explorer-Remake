using Clearspace.Models;

namespace Clearspace.Services;

/// <summary>
/// Bounded in-memory LRU cache for directory snapshots. Returning to a recently
/// visited location becomes a pointer swap; a background refresh then reconciles
/// it with disk without making navigation wait.
/// </summary>
internal static class FolderSnapshotCache
{
    private sealed record Entry(FileSystemItem[] Items, LinkedListNode<string> Node);

    private const int MaxFolders = 32;
    private const int MaxItems = 100_000;

    /// <summary>
    /// How many recent folders keep their decoded thumbnails alive.
    ///
    /// Snapshot entries hold FileSystemItem objects, and each of those holds a
    /// strong reference to its thumbnail bitmap. Without this, bounding the
    /// thumbnail cache achieves nothing: 32 folders of photos would pin hundreds
    /// of megabytes that the cache believes it has already evicted. Older
    /// snapshots keep their file listing, which is what makes going back instant;
    /// they just re-request pixels, which is fast.
    /// </summary>
    private const int PixelRetentionFolders = 3;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Recency = new();
    private static int _itemCount;

    internal static bool TryGet(string path, out IReadOnlyList<FileSystemItem> items)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(path, out var entry))
            {
                items = [];
                return false;
            }

            Recency.Remove(entry.Node);
            Recency.AddFirst(entry.Node);
            items = entry.Items;
            return true;
        }
    }

    internal static void Set(string path, IReadOnlyList<FileSystemItem> items)
    {
        if (items.Count > MaxItems)
            return;

        var snapshot = items.ToArray();

        lock (Gate)
        {
            if (Entries.Remove(path, out var existing))
            {
                Recency.Remove(existing.Node);
                _itemCount -= existing.Items.Length;
            }

            var node = Recency.AddFirst(path);
            Entries[path] = new Entry(snapshot, node);
            _itemCount += snapshot.Length;

            while (Entries.Count > MaxFolders || _itemCount > MaxItems)
            {
                var oldest = Recency.Last;
                if (oldest is null)
                    break;

                Recency.RemoveLast();
                if (Entries.Remove(oldest.Value, out var removed))
                    _itemCount -= removed.Items.Length;
            }

            ReleasePixelsBeyondRetention();
        }
    }

    /// <summary>Drops thumbnail references from every snapshot past the newest few.</summary>
    private static void ReleasePixelsBeyondRetention()
    {
        var index = 0;

        for (var node = Recency.First; node is not null; node = node.Next)
        {
            if (index++ < PixelRetentionFolders)
                continue;

            if (!Entries.TryGetValue(node.Value, out var entry))
                continue;

            foreach (var item in entry.Items)
            {
                // These items are not bound to anything: only the current folder's
                // list is, and that is always within the retained window.
                item.Thumbnail = null;
                item.GridPlaceholder = null;
            }
        }
    }
}
