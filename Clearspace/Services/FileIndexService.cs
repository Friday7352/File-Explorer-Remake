using System.Diagnostics;
using System.IO;
using Clearspace.Models;

namespace Clearspace.Services;

/// <summary>
/// Owns the file index: what is indexed, when it is built, and how it is queried.
///
/// The published set is swapped wholesale rather than mutated, so searches read it
/// without a lock and never see a volume half way through a rebuild. A build fills
/// its own <see cref="VolumeIndex"/> in isolation and is published only when it is
/// complete.
/// </summary>
public static class FileIndexService
{
    /// <summary>
    /// Read on every search, replaced on every publish. Volatile because the
    /// builder thread writes it and the UI thread reads it, and neither takes a
    /// lock to do so.
    /// </summary>
    private static volatile VolumeIndex[] _volumes = [];

    private static Thread? _worker;
    private static CancellationTokenSource? _cancellation;
    private static readonly Lock StartGate = new();

    /// <summary>Changes since the volumes were walked. Empty is the normal state.</summary>
    private static readonly IndexOverlay Overlay = new();

    private static FileIndexWatcher? _watcher;
    private static bool _watching;

    /// <summary>
    /// How much memory the whole index may hold, sized to the machine it is on.
    ///
    /// A fixed number is the wrong shape here. Eight million files with long names
    /// is around a gigabyte, and a cap set below that does not save memory - it
    /// throws the whole drive out of the index and puts search back to crawling,
    /// which is the opposite of the point. An eighth of available memory gives a
    /// large machine room to index everything and still keeps this from being the
    /// reason a small one starts paging.
    /// </summary>
    private static readonly long MaxIndexBytes = ResolveMemoryBudget();

    private static long ResolveMemoryBudget()
    {
        const long Minimum = 512L * 1024 * 1024;
        const long Maximum = 2048L * 1024 * 1024;

        try
        {
            var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

            return available <= 0
                ? Minimum * 2
                : Math.Clamp(available / 8, Minimum, Maximum);
        }
        catch (Exception)
        {
            return Minimum * 2;
        }
    }

    /// <summary>
    /// How stale a volume may be before it is walked again.
    ///
    /// While Clearspace runs, the watcher keeps the index current and this never
    /// matters. It exists for the window when Clearspace is *not* running, which
    /// nothing can observe: without the USN journal - which needs administrator
    /// rights - a rescan is the only way to learn what happened while the app was
    /// closed. An hour keeps that window short without walking the disk every time
    /// the app is opened twice in a row.
    /// </summary>
    private static readonly TimeSpan RebuildAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a build waits after launch. Loading a saved index does not wait at
    /// all - this is only about not walking the disk while the app is still
    /// opening its first folder.
    /// </summary>
    private static readonly TimeSpan StartupBuildDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Raised when the index becomes available, grows, or finishes building.
    ///
    /// Raised on the builder's own thread, not the UI thread. A subscriber that
    /// touches WPF must marshal to the dispatcher itself.
    /// </summary>
    public static event EventHandler? Changed;

    public static bool IsBuilding { get; private set; }

    /// <summary>
    /// True when the index can be believed without checking the disk.
    ///
    /// This is the switch that lets search stop crawling. It needs three things:
    /// something indexed, watchers actually running on it, and no overflow since
    /// they started. Lose any of them and search goes back to walking the disk,
    /// which is slower but never wrong.
    /// </summary>
    public static bool IsLive => _volumes.Length > 0 && _watching && !Overlay.Overflowed;

    /// <summary>How many changes have been seen since the volumes were walked.</summary>
    public static int PendingChanges => Overlay.Count;

    /// <summary>A short line for the status bar, or empty when there is nothing to say.</summary>
    public static string Status { get; private set; } = string.Empty;

    /// <summary>Total indexed entries across every volume.</summary>
    public static int Count
    {
        get
        {
            var total = 0;
            foreach (var volume in _volumes)
                total += volume.Count;
            return total;
        }
    }

    /// <summary>Roughly what the whole index costs in memory.</summary>
    public static long EstimatedBytes
    {
        get
        {
            var total = 0L;
            foreach (var volume in _volumes)
                total += volume.EstimatedBytes;
            return total;
        }
    }

    /// <summary>Volumes that were too large for the memory budget, so are still crawled.</summary>
    public static IReadOnlyList<string> SkippedRoots { get; private set; } = [];

    /// <summary>
    /// True when this path sits on a volume the index covers and can be trusted
    /// for. Callers use this to decide whether a disk walk is still needed.
    /// </summary>
    public static bool Covers(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IsLive)
            return false;

        foreach (var volume in _volumes)
        {
            if (path.StartsWith(volume.Root, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Loads whatever was saved, then builds anything missing or stale.
    ///
    /// Safe to call more than once; only the first call starts a worker.
    /// </summary>
    public static void Start()
    {
        lock (StartGate)
        {
            if (_worker is not null)
                return;

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;

            _worker = new Thread(() => Run(token))
            {
                IsBackground = true,
                Name = "Clearspace index",
                // Belt and braces: the thread also enters Windows background mode
                // inside Run, which is what actually lowers its disk priority.
                Priority = ThreadPriority.Lowest
            };

            _worker.Start();
        }
    }

    /// <summary>Stops any in-flight build and writes what exists to disk.</summary>
    public static void Stop()
    {
        try
        {
            _cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _watching = false;
        _watcher?.Dispose();
        _watcher = null;

        var volumes = _volumes;

        if (volumes.Length > 0)
            FileIndexStore.Save(volumes);
    }

    private static void Run(CancellationToken token)
    {
        try
        {
            // Loading is two large sequential reads, so this is the fast path and
            // it runs before anything is walked.
            Report("Loading index…");

            var loaded = FileIndexStore.Load();

            _watcher = new FileIndexWatcher(Overlay);
            _watcher.Desynchronised += (_, _) =>
                Report("Index out of date · searching by crawling until it rebuilds");

            if (loaded.Count > 0)
            {
                Publish([.. loaded]);
                StartWatching(loaded.Select(volume => volume.Root));
                Report($"Index ready · {Count:N0} items");
            }

            // The load above is not delayed: a machine that has indexed before
            // should be searchable as soon as its window appears, and reading the
            // saved index back is two large sequential reads.
            //
            // Only the *walk* waits. That is the expensive part, and it has no
            // business competing with the app's first seconds on disk.
            if (token.WaitHandle.WaitOne(StartupBuildDelay))
                return;

            // Nothing is waiting on the index, so it yields the disk to whatever
            // the user is actually doing. This is the difference between a build
            // that goes unnoticed and one that makes the machine feel busy.
            FileIndexBuilder.EnterBackgroundMode();

            try
            {
                BuildMissing(token);
            }
            finally
            {
                FileIndexBuilder.ExitBackgroundMode();
            }

            if (!token.IsCancellationRequested)
            {
                FileIndexStore.Save(_volumes);
                Report(Count > 0 ? $"Index ready · {Count:N0} items" : string.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception exception)
        {
            // The index is an accelerator. Search works without it, so nothing
            // here is ever worth taking down the app for.
            Trace.WriteLine($"Clearspace: file index failed. {exception}");
            Report(string.Empty);
        }
        finally
        {
            IsBuilding = false;
            Raise();
        }
    }

    private static void BuildMissing(CancellationToken token)
    {
        var skipped = new List<string>(SkippedRoots);

        foreach (var root in EnumerateIndexableRoots())
        {
            token.ThrowIfCancellationRequested();

            var existing = Array.Find(
                _volumes,
                volume => volume.Root.Equals(root, StringComparison.OrdinalIgnoreCase));

            if (existing is not null && DateTime.UtcNow - existing.BuiltUtc < RebuildAfter)
                continue;

            // Whatever is left of the budget once the volumes already held are
            // accounted for. A rebuild of a volume we already have gets to spend
            // what that volume is currently using, since it is about to replace it.
            var budget = MaxIndexBytes - EstimatedBytes + (existing?.EstimatedBytes ?? 0);

            if (budget <= 0)
            {
                if (!skipped.Contains(root, StringComparer.OrdinalIgnoreCase))
                    skipped.Add(root);

                continue;
            }

            var serial = FileIndexBuilder.GetSerialNumber(root);

            IsBuilding = true;
            Report($"Indexing {root}…");

            // Watched from before the walk starts, not after it finishes. A walk of
            // a large volume takes minutes, and anything created during it would
            // otherwise fall in the gap between the snapshot and the first event.
            // Changes seen during the walk may duplicate what the walk itself found;
            // search de-duplicates by path, so that costs nothing.
            StartWatching([root]);

            VolumeIndex? built;

            try
            {
                built = FileIndexBuilder.Build(
                    root,
                    serial,
                    budget,
                    count => Report($"Indexing {root}… {count:N0} items"),
                    token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Trace.WriteLine($"Clearspace: could not index {root}. {exception.Message}");
                continue;
            }

            if (built is null)
            {
                // Too large to hold. Said out loud rather than left as a silently
                // partial index, because search will eventually trust this.
                if (!skipped.Contains(root, StringComparer.OrdinalIgnoreCase))
                    skipped.Add(root);

                SkippedRoots = [.. skipped];
                Report($"{root} is too large to index · still searched by crawling");
                continue;
            }

            // Published as one atomic swap, so a search either sees the whole
            // volume or none of it - never a partial walk.
            var updated = _volumes
                .Where(volume => !volume.Root.Equals(root, StringComparison.OrdinalIgnoreCase))
                .Append(built)
                .ToArray();

            Publish(updated);
            SkippedRoots = [.. skipped];
            Report($"Index ready · {Count:N0} items");
        }

        IsBuilding = false;
    }

    private static void StartWatching(IEnumerable<string> roots)
    {
        if (_watcher is null)
            return;

        foreach (var root in roots)
            _watcher.Watch(root);

        _watching = true;
    }

    /// <summary>
    /// Local fixed drives only.
    ///
    /// Network shares are excluded deliberately: a crawl over SMB is slow enough
    /// to be felt even at background priority, and the result goes stale the
    /// moment somebody else touches the share. Removable drives are excluded
    /// because indexing something that will be unplugged is wasted work.
    /// </summary>
    private static IEnumerable<string> EnumerateIndexableRoots()
    {
        DriveInfo[] drives;

        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var drive in drives)
        {
            string root;

            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                    continue;

                root = drive.RootDirectory.FullName;
            }
            catch (Exception)
            {
                continue;
            }

            yield return root;
        }
    }

    /// <summary>
    /// Answers a query from memory. No disk access at all: names come from the
    /// pool, and size, dates and attributes were captured when the volume was
    /// walked, so a result is complete without a single stat call.
    /// </summary>
    public static IReadOnlyList<FileSystemItem> Search(
        SearchQuery query,
        IReadOnlyList<string> roots,
        bool showHidden,
        int limit,
        CancellationToken token)
    {
        var volumes = _volumes;

        if (volumes.Length == 0 || limit <= 0)
            return [];

        var terms = query.Terms;

        // A query with no name terms - "tag:work" on its own, say - has nothing
        // for a name index to match. Those are answered from the tag index.
        if (terms.Count == 0)
            return [];

        var folded = new string[terms.Count];

        for (var i = 0; i < terms.Count; i++)
            folded[i] = VolumeIndex.Fold(terms[i]);

        // Over-fetch, because structural filters (kind, extension, tags) are
        // applied after materialising and can reject most of a name match.
        var scanLimit = Math.Min(limit * 4, 200_000);
        var results = new List<FileSystemItem>();

        // Paths can arrive twice: once from the walk and once from a change seen
        // while the walk was still running.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var volume in volumes)
        {
            if (token.IsCancellationRequested || results.Count >= limit)
                break;

            var hits = volume.Search(folded, showHidden, false, false, scanLimit, token);

            foreach (var hit in hits)
            {
                if (results.Count >= limit)
                    break;

                var path = volume.GetPath(hit);

                if (path.Length == 0)
                    continue;

                // The index holds whole volumes, but a search is scoped: without
                // Everywhere on it means this folder and below, not the drive.
                if (!IsUnderAnyRoot(path, roots))
                    continue;

                // Deleted since the walk. The entry is still in the index - the
                // index is never edited - so the overlay is what removes it.
                if (Overlay.IsRemoved(path))
                    continue;

                if (!seen.Add(path))
                    continue;

                var item = Materialise(volume, hit, path);

                if (item is null || !query.Matches(item))
                    continue;

                results.Add(item);
            }
        }

        // And anything created since the walk, which the index has never seen.
        if (results.Count < limit && !token.IsCancellationRequested)
        {
            var recent = new List<FileSystemItem>();
            Overlay.CollectMatches(folded, showHidden, limit - results.Count, recent);

            foreach (var item in recent)
            {
                if (!IsUnderAnyRoot(item.FullPath, roots))
                    continue;

                if (!seen.Add(item.FullPath) || !query.Matches(item))
                    continue;

                results.Add(item);
            }
        }

        return results;
    }

    /// <summary>
    /// Checks that results still exist, and records the ones that do not so later
    /// searches skip them.
    ///
    /// This is the answer to the one gap a watcher cannot close. While Clearspace
    /// runs, deletions arrive as events; while it is closed, nothing is listening,
    /// so a file removed in the meantime is still sitting in the index when it
    /// loads. Rescanning the volume to find out would cost minutes. Checking the
    /// few thousand rows a search actually returned costs milliseconds, and it is
    /// the same answer where it matters - the user never sees an entry that is not
    /// in a result.
    ///
    /// Called off the UI thread, after the results are already on screen.
    /// </summary>
    public static IReadOnlyList<FileSystemItem> PruneMissing(IReadOnlyList<FileSystemItem> items)
    {
        var missing = new List<FileSystemItem>();

        foreach (var item in items)
        {
            try
            {
                if (item.IsFolder ? Directory.Exists(item.FullPath) : File.Exists(item.FullPath))
                    continue;
            }
            catch (Exception)
            {
                // Unreadable is not the same as gone; leave it alone.
                continue;
            }

            missing.Add(item);
            Overlay.OnDeleted(item.FullPath);
        }

        return missing;
    }

    /// <summary>
    /// Whether a path sits inside one of the search roots.
    ///
    /// The separator check is what stops "C:\Users\Sam" from claiming
    /// "C:\Users\Sammy": a prefix match alone is not containment.
    /// </summary>
    private static bool IsUnderAnyRoot(string path, IReadOnlyList<string> roots)
    {
        for (var i = 0; i < roots.Count; i++)
        {
            var root = roots[i];

            if (root.Length == 0 || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue;

            if (path.Length == root.Length ||
                root.EndsWith(Path.DirectorySeparatorChar) ||
                path[root.Length] == Path.DirectorySeparatorChar)
            {
                return true;
            }
        }

        return false;
    }

    private static FileSystemItem? Materialise(VolumeIndex volume, int index, string path)
    {
        try
        {
            // Copied rather than held by reference: forty bytes, once per result
            // that is actually returned, and it keeps this free of ref-local rules.
            var entry = volume.Entry(index);

            return new FileSystemItem
            {
                Name = volume.GetName(index),
                FullPath = path,
                Attributes = entry.Attributes,
                Size = entry.Size,
                DateModified = ToDateTime(entry.ModifiedTicks),
                DateCreated = ToDateTime(entry.CreatedTicks),
                IsInCloudRoot = CloudStorageService.IsCloudPath(path)
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Raw file times are stored, not DateTimes, so a build does not spend a
    /// million conversions on values nobody looks at. This is that conversion,
    /// done only for results actually returned.
    /// </summary>
    private static DateTime ToDateTime(long fileTime)
    {
        if (fileTime <= 0)
            return DateTime.MinValue;

        try
        {
            return DateTime.FromFileTime(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    private static void Publish(VolumeIndex[] volumes)
    {
        _volumes = volumes;
        Raise();
    }

    private static void Report(string status)
    {
        Status = status;
        Raise();
    }

    private static void Raise()
    {
        try
        {
            Changed?.Invoke(null, EventArgs.Empty);
        }
        catch (Exception)
        {
            // A subscriber's failure is not the index's problem.
        }
    }
}
