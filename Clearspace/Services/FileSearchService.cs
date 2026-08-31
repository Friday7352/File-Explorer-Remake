using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Clearspace.Models;
using Clearspace.Native;

namespace Clearspace.Services;

/// <summary>
/// Walks directory trees and reports matches as they are found.
///
/// Each volume gets its own queue and its own set of workers, sized to what that
/// device actually wants. This is the important part on a mixed machine. A single
/// shared pool has two failures: slow devices occupy worker slots that a fast one
/// could have used thousands of times over, and any level-by-level structure makes
/// the fast drives wait at a barrier for the slowest directory on the slowest disk.
///
/// Here an NVMe drive runs sixteen deep and never waits on a spinning disk, while
/// that disk reads two at a time and is not made to thrash. Volumes finish
/// independently and results stream in from whichever is fastest.
/// </summary>
internal static class FileSearchService
{
    private const int MaxDepth = 24;

    private sealed class VolumeQueue
    {
        public required string Root { get; init; }
        public required int Concurrency { get; init; }
        public ConcurrentQueue<(string Path, int Depth)> Pending { get; } = new();
        public SemaphoreSlim Available { get; } = new(0);

        /// <summary>
        /// Directories queued but not yet finished. Reaching zero is what tells the
        /// workers this volume is done; an empty queue alone is not enough, because
        /// a worker may still be about to enqueue children.
        /// </summary>
        public int Outstanding;
    }

    /// <summary>Returns true when the result cap was reached before the walk finished.</summary>
    public static bool Run(
        IReadOnlyList<string> roots,
        bool showHidden,
        Func<FileSystemItem, bool> matches,
        IProgress<IReadOnlyList<FileSystemItem>> progress,
        int maxResults,
        CancellationToken token)
    {
        if (roots.Count == 0)
            return false;

        // Stopping early on the cap cancels every worker on every volume at once.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stopToken = stop.Token;

        var total = 0;
        var capped = false;

        var buffer = new List<FileSystemItem>(256);
        var gate = new object();
        var sinceReport = Stopwatch.StartNew();

        var volumes = BuildVolumes(roots);
        var workers = new List<Task>();

        foreach (var volume in volumes)
        {
            for (var i = 0; i < volume.Concurrency; i++)
            {
                // LongRunning, not Task.Run, because every one of these workers
                // spends its life blocked on a directory read or on the semaphore.
                // Searching three drives at once asks for dozens of them, and taken
                // from the shared thread pool they starve it: the pool grows only
                // about one thread per second, so async continuations elsewhere in
                // the app - icon batches, thumbnails, the next navigation - end up
                // queued behind a crawl. LongRunning gives each its own thread and
                // leaves the pool alone, which is what keeps the rest of the window
                // responsive while a search runs.
                workers.Add(Task.Factory.StartNew(
                    () => Work(volume),
                    stopToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default));
            }
        }

        try
        {
            Task.WaitAll([.. workers], token);
        }
        catch (AggregateException exception) when (exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
            // Cancelled by a new query, by navigation, or by hitting the cap.
        }
        finally
        {
            foreach (var volume in volumes)
                volume.Available.Dispose();
        }

        token.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (buffer.Count > 0)
            {
                progress.Report([.. buffer]);
                buffer.Clear();
            }
        }

        return capped;

        void Work(VolumeQueue volume)
        {
            while (!stopToken.IsCancellationRequested)
            {
                // The timeout is what lets a worker notice the volume has finished
                // while it is parked waiting for work that will never arrive.
                if (!volume.Available.Wait(40, stopToken))
                {
                    if (Volatile.Read(ref volume.Outstanding) == 0)
                        return;

                    continue;
                }

                if (!volume.Pending.TryDequeue(out var entry))
                {
                    if (Volatile.Read(ref volume.Outstanding) == 0)
                        return;

                    continue;
                }

                try
                {
                    Process(volume, entry.Path, entry.Depth);
                }
                finally
                {
                    if (Interlocked.Decrement(ref volume.Outstanding) == 0)
                    {
                        // Wake every sibling so they can see the count hit zero and
                        // exit, rather than sitting out their full timeout.
                        try
                        {
                            volume.Available.Release(volume.Concurrency);
                        }
                        catch (ObjectDisposedException)
                        {
                            // Torn down already.
                        }
                        catch (SemaphoreFullException)
                        {
                        }
                    }
                }
            }
        }

        void Process(VolumeQueue volume, string directory, int depth)
        {
            List<FileSystemItem> entries;

            try
            {
                entries = DirectoryEnumerator.Enumerate(directory, showHidden, stopToken).ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Unreadable directory. A crawl from a drive root always hits
                // folders the current user has no rights to.
                return;
            }

            foreach (var item in entries)
            {
                // Reparse points are not descended into: junctions and symlinks
                // form cycles that would make this run forever.
                if (item.IsFolder &&
                    depth < MaxDepth &&
                    (item.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    Interlocked.Increment(ref volume.Outstanding);
                    volume.Pending.Enqueue((item.FullPath, depth + 1));

                    try
                    {
                        volume.Available.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }

                if (!matches(item))
                    continue;

                List<FileSystemItem>? flush = null;

                lock (gate)
                {
                    if (total >= maxResults)
                    {
                        capped = true;
                        stop.Cancel();
                        return;
                    }

                    buffer.Add(item);
                    total++;

                    if (buffer.Count >= 128 || sinceReport.ElapsedMilliseconds >= 200)
                    {
                        flush = [.. buffer];
                        buffer.Clear();
                        sinceReport.Restart();
                    }
                }

                // Reported outside the lock so a busy UI thread cannot stall every
                // other worker on every other volume.
                if (flush is not null)
                    progress.Report(flush);
            }
        }
    }

    /// <summary>
    /// Groups the roots by the volume they live on and sizes each queue for that
    /// device. Roots on the same volume share one queue, so searching both C:\ and
    /// a folder on C: does not double that drive's concurrency.
    /// </summary>
    private static List<VolumeQueue> BuildVolumes(IReadOnlyList<string> roots)
    {
        var byVolume = new Dictionary<string, VolumeQueue>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var key = VolumeProfiler.RootOf(root);

            if (key.Length == 0)
                key = root;

            if (!byVolume.TryGetValue(key, out var volume))
            {
                volume = new VolumeQueue
                {
                    Root = key,
                    Concurrency = VolumeProfiler.ConcurrencyFor(root)
                };

                byVolume[key] = volume;
            }

            Interlocked.Increment(ref volume.Outstanding);
            volume.Pending.Enqueue((root, 0));
            volume.Available.Release();
        }

        return [.. byVolume.Values];
    }
}
