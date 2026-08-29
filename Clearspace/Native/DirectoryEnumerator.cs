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

    /// <summary>Counts entries without materialising items. Used for folder size hints.</summary>
    public static int CountEntries(string directory, bool showHidden, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var _ in Enumerate(directory, showHidden, cancellationToken))
            count++;
        return count;
    }
}
