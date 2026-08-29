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

    public bool IsFolder => (Attributes & FileAttributes.Directory) != 0;

    public bool IsHidden => (Attributes & FileAttributes.Hidden) != 0;

    public bool IsShortcut => !IsFolder &&
        Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase);

    public string Extension => IsFolder ? string.Empty : Path.GetExtension(Name);

    public string SizeText => IsFolder ? string.Empty : FormatSize(Size);

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
        set => SetProperty(ref _icon, value);
    }

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
