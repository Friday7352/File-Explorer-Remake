using System.IO;
using Clearspace.Native;

namespace Clearspace.Services;

/// <summary>
/// Fills a <see cref="VolumeIndex"/> by walking a volume.
///
/// This is the part Everything does by reading the NTFS Master File Table, which
/// is dramatically faster and needs administrator rights to do. Clearspace runs
/// as the invoking user by design (see ARCHITECTURE.md), so it walks directories
/// instead and pays minutes where the MFT would cost seconds. That trade buys
/// three things worth more than the minutes: no elevation, no service to install,
/// and an index that works the same on exFAT, FAT32 and network shares as it does
/// on NTFS.
///
/// Since nobody is waiting on it, the build optimises for invisibility rather than
/// throughput: one thread per volume, in Windows background I/O mode, allocating
/// as little as possible so it does not provoke collections in the app around it.
/// </summary>
internal static class FileIndexBuilder
{
    private const int MaxDepth = 32;

    /// <summary>
    /// Lowers this thread's CPU *and* disk priority for the duration of a build.
    ///
    /// ThreadPriority.Lowest would only do the first, and a directory walk is
    /// bound by the disk. Without this the index is a background job that still
    /// makes the machine feel busy, which is the thing it must never do.
    /// </summary>
    public static void EnterBackgroundMode()
        => NativeMethods.SetThreadPriority(
            NativeMethods.GetCurrentThread(),
            NativeMethods.THREAD_MODE_BACKGROUND_BEGIN);

    public static void ExitBackgroundMode()
        => NativeMethods.SetThreadPriority(
            NativeMethods.GetCurrentThread(),
            NativeMethods.THREAD_MODE_BACKGROUND_END);

    /// <summary>
    /// Walks a volume and returns its finished index, or null when it would cost
    /// more memory than <paramref name="maxBytes"/> allows.
    ///
    /// Null rather than a truncated index, deliberately. A half-indexed volume is
    /// worse than an unindexed one: it looks complete, and the moment search starts
    /// trusting the index instead of crawling, every file past the cut-off would
    /// silently stop existing as far as the user could tell. Better to say the
    /// volume is too large and keep crawling it.
    ///
    /// Hidden and system files are indexed unconditionally; whether they are shown
    /// is decided at query time. Filtering them out here would mean rebuilding the
    /// whole volume every time that setting is toggled.
    /// </summary>
    public static VolumeIndex? Build(
        string root,
        uint serialNumber,
        long maxBytes,
        Action<int>? progress,
        CancellationToken token)
    {
        var index = new VolumeIndex(root, serialNumber);

        // The root is entry zero, and every path on the volume terminates here.
        var rootIndex = index.Add(-1, root.AsSpan(), 0, 0, 0, FileAttributes.Directory);

        var pending = new Stack<(string Path, int Parent, int Depth)>();
        pending.Push((root, rootIndex, 0));

        var sinceReport = 0;

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();

            var (directory, parent, depth) = pending.Pop();
            Scan(index, directory, parent, depth, pending, token);

            // Checked per directory rather than per file: overshooting by one
            // folder is nothing, and this keeps the inner loop clean.
            if (index.EstimatedBytes > maxBytes)
                return null;

            if (++sinceReport >= 64)
            {
                sinceReport = 0;
                progress?.Invoke(index.Count);
            }
        }

        index.Compact();
        progress?.Invoke(index.Count);
        return index;
    }

    private static void Scan(
        VolumeIndex index,
        string directory,
        int parent,
        int depth,
        Stack<(string Path, int Parent, int Depth)> pending,
        CancellationToken token)
    {
        var pattern = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory + "*"
            : directory + @"\*";

        using var handle = NativeMethods.FindFirstFileExW(
            pattern,
            NativeMethods.FINDEX_INFO_LEVELS.FindExInfoBasic,
            out var data,
            NativeMethods.FINDEX_SEARCH_OPS.FindExSearchNameMatch,
            IntPtr.Zero,
            NativeMethods.FIND_FIRST_EX_LARGE_FETCH);

        // A walk from a drive root always meets folders this user cannot read.
        // That is ordinary, not an error, and the rest of the volume still indexes.
        if (handle.IsInvalid)
            return;

        do
        {
            token.ThrowIfCancellationRequested();

            var name = data.cFileName;

            if (name is "." or "..")
                continue;

            var attributes = data.dwFileAttributes;
            var size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;

            // Raw file times are stored as-is and only turned into DateTime for
            // results actually displayed. A million conversions during a build
            // would be a million pieces of work nobody ever looks at.
            var child = index.Add(
                parent,
                name.AsSpan(),
                size,
                data.ftLastWriteTime.ToLong(),
                data.ftCreationTime.ToLong(),
                attributes);

            if ((attributes & FileAttributes.Directory) != 0 &&
                depth < MaxDepth &&
                (attributes & FileAttributes.ReparsePoint) == 0)
            {
                // Reparse points are skipped for the same reason the search crawl
                // skips them: junctions and symlinks form cycles.
                pending.Push((Join(directory, name), child, depth + 1));
            }
        }
        while (NativeMethods.FindNextFileW(handle, out data));
    }

    private static string Join(string directory, string name)
        => directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory + name
            : directory + Path.DirectorySeparatorChar + name;

    /// <summary>
    /// A volume's serial number, or zero when Windows will not say. Stored with a
    /// saved index so a reformatted drive is rebuilt rather than trusted.
    /// </summary>
    public static uint GetSerialNumber(string root)
    {
        try
        {
            return NativeMethods.GetVolumeInformationW(
                root, IntPtr.Zero, 0, out var serial, out _, out _, IntPtr.Zero, 0)
                ? serial
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
