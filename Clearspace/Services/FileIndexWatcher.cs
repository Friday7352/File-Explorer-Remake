using System.Diagnostics;
using System.IO;

namespace Clearspace.Services;

/// <summary>
/// Keeps the index honest by watching the volumes it covers.
///
/// This is the piece that lets search stop crawling. Without it the index is a
/// snapshot of whenever it was last walked, so a file created since then would be
/// missing and search has to walk the disk anyway to be sure. With it, changes
/// land in the overlay as they happen and the index can be trusted.
///
/// Everything solves the same problem with the NTFS USN journal, which is both
/// better and unavailable without administrator rights. FileSystemWatcher wraps
/// ReadDirectoryChangesW, which is the same mechanism at a higher level and
/// carries the same limitation: a burst of changes larger than the buffer is
/// dropped by the OS, not queued. That case is detectable, and when it happens
/// the honest answer is to stop claiming the index is current.
/// </summary>
internal sealed class FileIndexWatcher : IDisposable
{
    // The documented ceiling for a watch that might cross a network path, and
    // large enough that only genuinely heavy churn - an installer, an unzip, a
    // build - will overrun it.
    private const int BufferSize = 64 * 1024;

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly IndexOverlay _overlay;

    public FileIndexWatcher(IndexOverlay overlay) => _overlay = overlay;

    /// <summary>Raised when changes were lost and the index can no longer be trusted.</summary>
    public event EventHandler? Desynchronised;

    public void Watch(string root)
    {
        try
        {
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = BufferSize,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
            };

            // Only creations, deletions and renames are watched. Size and
            // timestamp changes are deliberately left out: they do not affect
            // whether a name matches a search, and including them turns every
            // file write on the machine into an event this has to process.
            watcher.Created += (_, e) => _overlay.OnCreated(e.FullPath);
            watcher.Deleted += (_, e) => _overlay.OnDeleted(e.FullPath);
            watcher.Renamed += (_, e) => _overlay.OnRenamed(e.OldFullPath, e.FullPath);
            watcher.Error += OnError;

            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception exception)
        {
            // A volume that cannot be watched cannot be trusted either, so this
            // takes the whole index out of trusted mode rather than leaving a
            // quiet hole in it.
            Trace.WriteLine($"Clearspace: cannot watch {root}. {exception.Message}");
            _overlay.MarkOverflowed();
            Desynchronised?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // Windows dropped notifications because they arrived faster than the
        // buffer drained. There is no way to recover what was missed, so the
        // index is now out of date by an unknown amount and search goes back to
        // crawling until the next rebuild.
        Trace.WriteLine($"Clearspace: watcher overflow. {e.GetException()?.Message}");
        _overlay.MarkOverflowed();
        Desynchronised?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception)
            {
                // Shutting down.
            }
        }

        _watchers.Clear();
    }
}
