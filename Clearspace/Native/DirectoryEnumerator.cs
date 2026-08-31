using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Clearspace.Models;
using Clearspace.Services;

namespace Clearspace.Native;

/// <summary>
/// Streams a directory listing straight out of FindFirstFileEx.
///
/// Why not Directory.EnumerateFileSystemEntries: that returns strings, so every item
/// then needs a second stat call to get size, attributes, and timestamps. The find
/// data already carries all of it, so one pass over the directory produces finished
/// items with no per-item disk access.
/// </summary>
internal static class DirectoryEnumerator
{
    public static IEnumerable<FileSystemItem> Enumerate(
        string directory,
        bool showHidden,
        CancellationToken cancellationToken)
    {
        var pattern = Path.Combine(directory, "*");

        // Resolved once for the whole directory. Sync membership is a property of
        // the location, so asking per item would repeat the same string comparison
        // a hundred thousand times in a large folder for one identical answer.
        var inCloudRoot = CloudStorageService.IsCloudPath(directory);

        using var handle = NativeMethods.FindFirstFileExW(
            pattern,
            NativeMethods.FINDEX_INFO_LEVELS.FindExInfoBasic,
            out var data,
            NativeMethods.FINDEX_SEARCH_OPS.FindExSearchNameMatch,
            IntPtr.Zero,
            NativeMethods.FIND_FIRST_EX_LARGE_FETCH);

        if (handle.IsInvalid)
        {
            // GetLastWin32Error has to be read here, before anything else runs on
            // this thread. Silently yielding nothing was the old behaviour, and it
            // is why a protected folder such as System Volume Information looked
            // like an ordinary empty one instead of a folder we were refused.
            var error = Marshal.GetLastWin32Error();

            if (error is NativeMethods.ERROR_FILE_NOT_FOUND or NativeMethods.ERROR_NO_MORE_FILES)
                yield break;

            throw Translate(error, directory);
        }

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (data.cFileName is "." or "..")
                continue;

            var attributes = data.dwFileAttributes;

            if (!showHidden &&
                (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                continue;

            yield return FileSystemItem.FromFindData(directory, in data, inCloudRoot);
        }
        while (NativeMethods.FindNextFileW(handle, out data));
    }

    /// <summary>
    /// Walks a directory and everything beneath it, depth first.
    ///
    /// Two safeguards matter here. Reparse points are not followed, because
    /// junctions and symlinks form cycles that would otherwise make this run
    /// forever; and a directory that cannot be read is skipped rather than
    /// throwing, since a crawl from a drive root will always hit folders the
    /// current user has no rights to.
    /// </summary>
    public static IEnumerable<FileSystemItem> EnumerateTree(
        string root,
        bool showHidden,
        CancellationToken cancellationToken,
        int maxDepth = 24)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (directory, depth) = pending.Pop();
            List<FileSystemItem> entries;

            try
            {
                // Materialised inside the try so a failure part-way through one
                // directory cannot abandon the whole walk. yield return is not
                // allowed inside a try with a catch, hence the buffer.
                entries = Enumerate(directory, showHidden, cancellationToken).ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var item in entries)
            {
                yield return item;

                if (item.IsFolder &&
                    depth < maxDepth &&
                    (item.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    pending.Push((item.FullPath, depth + 1));
                }
            }
        }
    }

    /// <summary>Counts entries without materialising items. Used for folder size hints.</summary>
    public static int CountEntries(string directory, bool showHidden, CancellationToken cancellationToken)
    {
        var count = 0;

        try
        {
            foreach (var _ in Enumerate(directory, showHidden, cancellationToken))
                count++;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A hint, not a listing. A folder we cannot read simply has no count.
            return 0;
        }

        return count;
    }

    /// <summary>
    /// Turns a find error into the managed exception the rest of the app already
    /// handles, so callers keep catching UnauthorizedAccessException rather than
    /// learning Win32 error numbers.
    /// </summary>
    private static Exception Translate(int error, string directory) => error switch
    {
        NativeMethods.ERROR_ACCESS_DENIED =>
            new UnauthorizedAccessException($"Access to '{directory}' is denied."),
        NativeMethods.ERROR_PATH_NOT_FOUND or NativeMethods.ERROR_INVALID_NAME =>
            new DirectoryNotFoundException($"Could not find '{directory}'."),
        NativeMethods.ERROR_NOT_READY =>
            new IOException("The drive is not ready."),
        NativeMethods.ERROR_BAD_NETPATH =>
            new IOException("The network location is unavailable."),
        _ => new IOException(new Win32Exception(error).Message, error)
    };
}
