using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Clearspace.Services;

/// <summary>
/// Reads and writes the index to disk.
///
/// This is what makes the slow first build acceptable. Rebuilding a million-file
/// volume is minutes; reading the same thing back is two large sequential reads
/// into buffers that are already the right shape, which is well under a second.
/// The build happens once; every launch after that just loads.
///
/// The entry array and the name pool go to disk as raw bytes, since both are
/// already flat blocks of unmanaged data. The folded pool is not saved: lower
/// casing the pool on load is faster than reading another forty megabytes.
/// </summary>
internal static class FileIndexStore
{
    private const int Magic = 0x58495343; // 'CSIX'

    /// <summary>Bump this on any layout change; an older file is then rebuilt, not misread.</summary>
    private const int FormatVersion = 1;

    // A corrupt or truncated file must not be able to talk us into allocating
    // gigabytes. Nothing legitimate comes close to these.
    private const int MaxEntries = 40_000_000;
    private const int MaxPool = 800_000_000;

    private static readonly string Directory_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clearspace");

    internal static string FilePath => Path.Combine(Directory_, "index.db");

    public static void Save(IReadOnlyList<VolumeIndex> volumes)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory_);

            // Written beside the real file and moved into place, so a crash
            // half way through leaves the previous index intact rather than a
            // truncated one that fails to load.
            var temporary = FilePath + ".tmp";

            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(volumes.Count);

                foreach (var volume in volumes)
                {
                    writer.Write(volume.Root);
                    writer.Write(volume.SerialNumber);
                    writer.Write(volume.BuiltUtc.Ticks);
                    writer.Write(volume.Count);
                    writer.Write(volume.PoolLength);
                    writer.Flush();

                    stream.Write(MemoryMarshal.AsBytes(volume.Entries.AsSpan(0, volume.Count)));
                    stream.Write(MemoryMarshal.AsBytes(volume.Names.AsSpan(0, volume.PoolLength)));
                }
            }

            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception exception)
        {
            // An index that cannot be saved costs a rebuild next launch, nothing
            // more. It is never worth failing anything else over.
            Trace.WriteLine($"Clearspace: could not save the file index. {exception.Message}");
        }
    }

    public static List<VolumeIndex> Load()
    {
        var volumes = new List<VolumeIndex>();

        try
        {
            if (!File.Exists(FilePath))
                return volumes;

            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

            if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion)
                return volumes;

            var count = reader.ReadInt32();

            if (count is < 0 or > 64)
                return volumes;

            for (var i = 0; i < count; i++)
            {
                var root = reader.ReadString();
                var serial = reader.ReadUInt32();
                var builtTicks = reader.ReadInt64();
                var entryCount = reader.ReadInt32();
                var poolLength = reader.ReadInt32();

                if (entryCount is < 0 or > MaxEntries || poolLength is < 0 or > MaxPool)
                    return volumes;

                var entries = new IndexEntry[Math.Max(1, entryCount)];
                var names = new char[Math.Max(1, poolLength)];

                stream.ReadExactly(MemoryMarshal.AsBytes(entries.AsSpan(0, entryCount)));
                stream.ReadExactly(MemoryMarshal.AsBytes(names.AsSpan(0, poolLength)));

                // The drive this describes may have been reformatted, or its
                // letter handed to something else entirely, since it was written.
                // Serial numbers are how that is noticed rather than assumed.
                if (FileIndexBuilder.GetSerialNumber(root) != serial)
                    continue;

                volumes.Add(new VolumeIndex(
                    root,
                    serial,
                    new DateTime(builtTicks, DateTimeKind.Utc),
                    entries,
                    entryCount,
                    names,
                    poolLength));
            }
        }
        catch (Exception exception)
        {
            // Truncated, corrupt, or written by a different build. Start over
            // rather than refuse to launch.
            Trace.WriteLine($"Clearspace: could not load the file index. {exception.Message}");
            return [];
        }

        return volumes;
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
        }
        catch (Exception)
        {
            // Nothing to do about it; the version guard will reject it anyway.
        }
    }
}
