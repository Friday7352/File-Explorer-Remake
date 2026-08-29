using System.IO;
using System.Windows.Media;
using Clearspace.Native;
using Clearspace.Services;

namespace Clearspace.Models;

/// <summary>
/// One row in the file list. Constructed entirely from find data, so creating one
/// costs no disk access. Icon is filled in afterwards from a cache.
/// </summary>
public sealed class FileSystemItem : ObservableObject
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public FileAttributes Attributes { get; init; }

    /// <summary>Size in bytes. Zero for folders; use <see cref="SizeText"/> for display.</summary>
    public long Size { get; init; }

    public DateTime DateModified { get; init; }

    public DateTime DateCreated { get; init; }

    /// <summary>True for the virtual items shown on My PC and Network.</summary>
    public bool IsDriveRoot { get; init; }

    public string? DriveKind { get; init; }

    public long DriveTotalSpace { get; init; }

    public long DriveAvailableSpace { get; init; }

    public bool IsFolder => (Attributes & FileAttributes.Directory) != 0;

    public bool IsStandardFolder => IsFolder && !IsDriveRoot;

    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;

    public bool IsShortcut => !IsFolder &&
        Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase);

    public string Extension => IsFolder ? string.Empty : Path.GetExtension(Name);

    public string SizeText => IsDriveRoot
        ? $"{FormatSize(DriveAvailableSpace)} free of {FormatSize(DriveTotalSpace)}"
        : IsFolder ? string.Empty : FormatSize(Size);

    public double DriveUsagePercent => DriveTotalSpace <= 0
        ? 0
        : Math.Clamp((DriveTotalSpace - DriveAvailableSpace) * 100d / DriveTotalSpace, 0, 100);

    public Brush DriveUsageBrush => DriveUsagePercent switch
    {
        >= 90 => new SolidColorBrush(Color.FromRgb(214, 95, 84)),
        >= 75 => new SolidColorBrush(Color.FromRgb(211, 161, 95)),
        _ => new SolidColorBrush(Color.FromRgb(112, 178, 132))
    };

    public string DateModifiedText => DateModified == DateTime.MinValue
        ? string.Empty
        : DateModified.ToString("g");

    private string? _typeName;
    public string TypeName
    {
        get => _typeName ??= IconService.GetTypeName(this);
        set => SetProperty(ref _typeName, value);
    }

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (SetProperty(ref _icon, value))
            {
                OnPropertyChanged(nameof(DisplayImage));
                OnPropertyChanged(nameof(GridImage));
            }
        }
    }

    private ImageSource? _gridPlaceholder;
    public ImageSource? GridPlaceholder
    {
        get => _gridPlaceholder;
        internal set
        {
            if (SetProperty(ref _gridPlaceholder, value))
                OnPropertyChanged(nameof(GridImage));
        }
    }

    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (SetProperty(ref _thumbnail, value))
            {
                OnPropertyChanged(nameof(DisplayImage));
                OnPropertyChanged(nameof(GridImage));
                OnPropertyChanged(nameof(HasThumbnail));
            }
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>
    /// What a tile actually shows: the real thumbnail once it arrives, and the
    /// cached type icon until then, so tiles never render empty.
    /// </summary>
    public ImageSource? DisplayImage => _thumbnail ?? _icon;

    /// <summary>Grid-specific image with a crisp vector shown while previews load.</summary>
    public ImageSource? GridImage => _thumbnail ?? _gridPlaceholder ?? _icon;

    internal static FileSystemItem FromFindData(string directory, in NativeMethods.WIN32_FIND_DATA data)
    {
        var name = data.cFileName;
        var size = ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;

        return new FileSystemItem
        {
            Name = name,
            FullPath = Path.Combine(directory, name),
            Attributes = data.dwFileAttributes,
            Size = size,
            DateModified = ToDateTime(data.ftLastWriteTime),
            DateCreated = ToDateTime(data.ftCreationTime)
        };
    }

    internal static FileSystemItem FromDrive(DriveInfo drive)
    {
        var root = drive.RootDirectory.FullName;
        var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel;
        var kind = drive.DriveType == DriveType.Network ? "Network drive" : "Local drive";

        return new FileSystemItem
        {
            Name = $"{label} ({root.TrimEnd('\\')})",
            FullPath = root,
            Attributes = FileAttributes.Directory,
            IsDriveRoot = true,
            DriveKind = kind,
            DriveTotalSpace = drive.TotalSize,
            DriveAvailableSpace = drive.AvailableFreeSpace
        };
    }

    /// <summary>Builds a lightweight hub tile for a known or pinned location.</summary>
    internal static FileSystemItem? FromLocation(string path, string? displayName = null)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var isFolder = (attributes & FileAttributes.Directory) != 0;
            var name = displayName ?? Path.GetFileName(path.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(name))
                name = path;

            if (isFolder)
            {
                var info = new DirectoryInfo(path);
                return new FileSystemItem
                {
                    Name = name,
                    FullPath = path,
                    Attributes = attributes,
                    DateModified = info.LastWriteTime,
                    DateCreated = info.CreationTime
                };
            }

            var file = new FileInfo(path);
            return new FileSystemItem
            {
                Name = name,
                FullPath = path,
                Attributes = attributes,
                Size = file.Length,
                DateModified = file.LastWriteTime,
                DateCreated = file.CreationTime
            };
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static DateTime ToDateTime(NativeMethods.FILETIME fileTime)
    {
        var ticks = fileTime.ToLong();
        if (ticks <= 0)
            return DateTime.MinValue;

        try
        {
            return DateTime.FromFileTime(ticks);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.MinValue;
        }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 0)
            return string.Empty;

        // Explorer reports in KB for anything under a megabyte, rounded up.
        if (bytes < 1024)
            return bytes == 0 ? "0 KB" : "1 KB";

        string[] units = ["KB", "MB", "GB", "TB", "PB"];
        double value = bytes / 1024d;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{Math.Ceiling(value):N0} {units[unit]}"
            : $"{value:N1} {units[unit]}";
    }

    public override string ToString() => FullPath;
}
