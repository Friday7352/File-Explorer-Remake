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

    public bool IsAudio => !IsFolder && MediaTypes.IsAudio(Extension);

    public bool IsImageFile => !IsFolder && MediaTypes.IsImage(Extension);

    // ---------- Tags ----------

    private IReadOnlyList<TagDefinition> _tags = [];

    /// <summary>Tags attached to this path, resolved for display.</summary>
    public IReadOnlyList<TagDefinition> Tags
    {
        get => _tags;
        internal set
        {
            if (SetProperty(ref _tags, value))
            {
                OnPropertyChanged(nameof(HasTags));
                OnPropertyChanged(nameof(TagNames));
            }
        }
    }

    public bool HasTags => _tags.Count > 0;

    public string TagNames => string.Join(", ", _tags.Select(tag => tag.Name));

    /// <summary>Re-reads this item's tags from the store.</summary>
    internal void RefreshTags() => Tags = TagService.TagsFor(FullPath);

    // ---------- Audio tags, filled in lazily for Music folders ----------

    private string? _mediaTitle;
    private string? _artist;
    private string? _album;
    private uint _trackNumber;
    private TimeSpan _duration;

    public bool HasMediaInfo { get; private set; }

    /// <summary>The tagged title when there is one, otherwise the bare file name.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(_mediaTitle)
        ? Path.GetFileNameWithoutExtension(Name)
        : _mediaTitle;

    public string Artist => _artist ?? string.Empty;

    public string Album => _album ?? string.Empty;

    public string TrackNumberText => _trackNumber > 0 ? _trackNumber.ToString() : string.Empty;

    public uint TrackNumber => _trackNumber;

    public TimeSpan Duration => _duration;

    public string DurationText => _duration <= TimeSpan.Zero
        ? string.Empty
        : _duration.TotalHours >= 1
            ? _duration.ToString(@"h\:mm\:ss")
            : _duration.ToString(@"m\:ss");

    private bool _isNowPlaying;
    /// <summary>Drives the row highlight for the track the player is on.</summary>
    public bool IsNowPlaying
    {
        get => _isNowPlaying;
        set => SetProperty(ref _isNowPlaying, value);
    }

    internal void ApplyMediaInfo(MediaPropertyService.MediaInfo info)
    {
        _mediaTitle = info.Title;
        _artist = info.Artist;
        _album = info.Album;
        _trackNumber = info.TrackNumber;
        _duration = info.Duration;
        HasMediaInfo = true;

        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(Artist));
        OnPropertyChanged(nameof(Album));
        OnPropertyChanged(nameof(TrackNumberText));
        OnPropertyChanged(nameof(TrackNumber));
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(HasMediaInfo));
    }

    public string Extension => IsFolder ? string.Empty : Path.GetExtension(Name);

    public string SizeText => IsDriveRoot
        ? $"{FormatSize(DriveAvailableSpace)} free of {FormatSize(DriveTotalSpace)}"
        : IsFolder ? string.Empty : FormatSize(Size);

    public double DriveUsagePercent => DriveTotalSpace <= 0
        ? 0
        : Math.Clamp((DriveTotalSpace - DriveAvailableSpace) * 100d / DriveTotalSpace, 0, 100);

    // Frozen and shared. A property getter that returns a new brush allocates on
    // every binding evaluation, and an unfrozen brush forces WPF to track it for
    // changes on the UI thread instead of reusing one render resource.
    private static readonly Brush DriveFullBrush = Frozen(Color.FromRgb(214, 95, 84));
    private static readonly Brush DriveWarnBrush = Frozen(Color.FromRgb(211, 161, 95));
    private static readonly Brush DriveOkBrush = Frozen(Color.FromRgb(112, 178, 132));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public Brush DriveUsageBrush => DriveUsagePercent switch
    {
        >= 90 => DriveFullBrush,
        >= 75 => DriveWarnBrush,
        _ => DriveOkBrush
    };

    public string DateModifiedText => DateModified == DateTime.MinValue
        ? string.Empty
        : DateModified.ToString("g");

    public string DateCreatedText => DateCreated == DateTime.MinValue
        ? string.Empty
        : DateCreated.ToString("g");

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
