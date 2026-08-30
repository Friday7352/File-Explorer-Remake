using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using Clearspace.Native;

namespace Clearspace.Services;

public enum VolumeKind
{
    Unknown,
    Hdd,
    Ssd,
    Nvme,
    Removable,
    Network
}

/// <summary>
/// Works out what kind of storage a path lives on, so a search can pick a sensible
/// number of concurrent reads for it.
///
/// This matters on a mixed machine. Directory enumeration is dominated by waiting,
/// not by data volume, so the right amount of concurrency is entirely a property of
/// the device: an NVMe drive has many hardware queues and wants a deep pipeline, a
/// spinning disk has one head and gets slower as concurrency rises, and a network
/// share is pure round-trip latency and benefits most of all.
///
/// Windows is asked directly. DriveInfo only reports Fixed, Network or Removable,
/// which cannot tell an SSD from an HDD.
/// </summary>
public static class VolumeProfiler
{
    private static readonly ConcurrentDictionary<string, VolumeKind> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Concurrent directory reads to keep in flight for a path's volume.</summary>
    public static int ConcurrencyFor(string path) => Classify(path) switch
    {
        // Many hardware queues; the pipeline has to be deep to keep it busy.
        VolumeKind.Nvme => 16,

        // NCQ handles 32 outstanding commands and there is no seek cost.
        VolumeKind.Ssd => 8,

        // Pure latency: every request is a round trip regardless of the disk
        // behind it, so overlapping them is the whole win.
        VolumeKind.Network => 8,

        // One head. Concurrent random reads make it seek between them, which is
        // usually slower than simply asking in order.
        VolumeKind.Hdd => 2,

        VolumeKind.Removable => 2,

        // Enough to help if it is solid state, not enough to hurt if it is not.
        _ => 4
    };

    public static VolumeKind Classify(string path)
    {
        var root = RootOf(path);

        if (root.Length == 0)
            return VolumeKind.Unknown;

        return Cache.GetOrAdd(root, Detect);
    }

    /// <summary>The volume key for a path: a drive root, or a UNC server and share.</summary>
    public static string RootOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                // Treat one share as one device: \\server\share
                var parts = path.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : path;
            }

            return Path.GetPathRoot(path) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static VolumeKind Detect(string root)
    {
        if (root.StartsWith(@"\\", StringComparison.Ordinal))
            return VolumeKind.Network;

        try
        {
            var drive = new DriveInfo(root);

            if (drive.DriveType == DriveType.Network)
                return VolumeKind.Network;

            if (drive.DriveType is DriveType.CDRom or DriveType.Ram)
                return VolumeKind.Removable;
        }
        catch (Exception)
        {
            // Fall through and ask the device itself.
        }

        var letter = root.TrimEnd('\\', '/');
        if (letter.Length != 2 || letter[1] != ':')
            return VolumeKind.Unknown;

        try
        {
            using var handle = NativeMethods.CreateFileW(
                $@"\\.\{letter}",
                NativeMethods.NO_ACCESS,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero);

            if (handle.IsInvalid)
                return VolumeKind.Unknown;

            var bus = ReadBusType(handle);

            if (bus == NativeMethods.BusTypeNvme)
                return VolumeKind.Nvme;

            if (bus is NativeMethods.BusTypeUsb or NativeMethods.BusTypeSd or NativeMethods.BusTypeMmc)
                return VolumeKind.Removable;

            return ReadSeekPenalty(handle) switch
            {
                true => VolumeKind.Hdd,
                false => VolumeKind.Ssd,
                _ => VolumeKind.Unknown
            };
        }
        catch (Exception)
        {
            return VolumeKind.Unknown;
        }
    }

    /// <summary>
    /// STORAGE_DEVICE_DESCRIPTOR is variable length with trailing strings, so the
    /// fixed header is read as raw bytes. BusType sits at offset 28.
    /// </summary>
    private static int ReadBusType(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var buffer = Query(handle, NativeMethods.StorageDeviceProperty, 1024);

        if (buffer is null || buffer.Length < 32)
            return -1;

        return BitConverter.ToInt32(buffer, 28);
    }

    /// <summary>
    /// DEVICE_SEEK_PENALTY_DESCRIPTOR: Version, Size, then the flag at offset 8.
    /// Null when the device declines to answer, which some USB bridges do.
    /// </summary>
    private static bool? ReadSeekPenalty(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var buffer = Query(handle, NativeMethods.StorageDeviceSeekPenaltyProperty, 16);

        if (buffer is null || buffer.Length < 9)
            return null;

        return buffer[8] != 0;
    }

    private static byte[]? Query(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int propertyId, int size)
    {
        var output = Marshal.AllocHGlobal(size);

        try
        {
            var query = new NativeMethods.STORAGE_PROPERTY_QUERY
            {
                PropertyId = propertyId,
                QueryType = NativeMethods.PropertyStandardQuery
            };

            var ok = NativeMethods.DeviceIoControl(
                handle,
                NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY,
                ref query,
                Marshal.SizeOf<NativeMethods.STORAGE_PROPERTY_QUERY>(),
                output,
                size,
                out var returned,
                IntPtr.Zero);

            if (!ok || returned <= 0)
                return null;

            var buffer = new byte[returned];
            Marshal.Copy(output, buffer, 0, returned);
            return buffer;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(output);
        }
    }

    /// <summary>Human-readable label, used in the search status line.</summary>
    public static string Describe(VolumeKind kind) => kind switch
    {
        VolumeKind.Nvme => "NVMe",
        VolumeKind.Ssd => "SSD",
        VolumeKind.Hdd => "HDD",
        VolumeKind.Network => "network",
        VolumeKind.Removable => "removable",
        _ => "unknown"
    };
}
