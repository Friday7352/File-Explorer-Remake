using System.IO;
using Clearspace.Models;

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

        using var handle = NativeMethods.FindFirstFileExW(
            pattern,
            NativeMethods.FINDEX_INFO_LEVELS.FindExInfoBasic,
            out var data,
            NativeMethods.FINDEX_SEARCH_OPS.FindExSearchNameMatch,
            IntPtr.Zero,
            NativeMethods.FIND_FIRST_EX_LARGE_FETCH);

        if (handle.IsInvalid)
            yield break;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (data.cFileName is "." or "..")
                continue;

            var attributes = data.dwFileAttributes;

            if (!showHidden &&
                (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                continue;

            yield return FileSystemItem.FromFindData(directory, in data);
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
        foreach (var _ in Enumerate(directory, showHidden, cancellationToken))
            count++;
        return count;
    }
}
