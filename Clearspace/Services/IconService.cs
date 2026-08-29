using System.Collections.Concurrent;
using System.IO;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clearspace.Models;
using Clearspace.Native;

namespace Clearspace.Services;

/// <summary>
/// Resolves list icons.
///
/// The important part is the cache key. For an ordinary file the icon is determined
/// entirely by its extension, so a folder containing 80,000 .jpg files needs exactly
/// one shell call, not 80,000. SHGFI_USEFILEATTRIBUTES also tells the shell to answer
/// from the registry rather than opening the file, so no disk access happens at all.
///
/// Types that carry their own icon (executables, shortcuts, icons) bypass the cache.
/// </summary>
public static class IconService
{
    private static readonly ConcurrentDictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> TypeNameCache = new(StringComparer.OrdinalIgnoreCase);

    private const string FolderKey = "\\__folder__";

    private static readonly HashSet<string> SelfIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".cur", ".ani", ".msi", ".scr", ".cpl", ".url"
    };

    public static ImageSource? GetIcon(FileSystemItem item)
    {
        if (item.IsFolder)
            return IconCache.GetOrAdd(FolderKey, _ => LoadIcon(item.FullPath, isFolder: true, useAttributes: true));

        var extension = item.Extension;

        // No extension means the shell has nothing to key off; treat it as generic.
        if (string.IsNullOrEmpty(extension))
            return IconCache.GetOrAdd(".__none__", _ => LoadIcon("file", isFolder: false, useAttributes: true));

        if (SelfIconExtensions.Contains(extension))
            return LoadIcon(item.FullPath, isFolder: false, useAttributes: false);

        return IconCache.GetOrAdd(extension, ext => LoadIcon("file" + ext, isFolder: false, useAttributes: true));
    }

    public static string GetTypeName(FileSystemItem item)
    {
        if (item.IsFolder)
            return "File folder";

        var extension = item.Extension;
        if (string.IsNullOrEmpty(extension))
            return "File";

        return TypeNameCache.GetOrAdd(extension, ext =>
        {
            var info = new NativeMethods.SHFILEINFO();
            var result = NativeMethods.SHGetFileInfoW(
                "file" + ext,
                NativeMethods.FILE_ATTRIBUTE_NORMAL,
                ref info,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
                NativeMethods.SHGFI_TYPENAME | NativeMethods.SHGFI_USEFILEATTRIBUTES);

            if (result == IntPtr.Zero || string.IsNullOrWhiteSpace(info.szTypeName))
                return ext.TrimStart('.').ToUpperInvariant() + " File";

            return info.szTypeName;
        });
    }

    private static ImageSource? LoadIcon(string path, bool isFolder, bool useAttributes)
    {
        var info = new NativeMethods.SHFILEINFO();

        var flags = NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON;
        if (useAttributes)
            flags |= NativeMethods.SHGFI_USEFILEATTRIBUTES;

        var attributes = isFolder
            ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY
            : NativeMethods.FILE_ATTRIBUTE_NORMAL;

        var result = NativeMethods.SHGetFileInfoW(
            path,
            attributes,
            ref info,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
            flags);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());

            // Freezing lets the same instance be shared across every row and any thread.
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            NativeMethods.DestroyIcon(info.hIcon);
        }
    }

    /// <summary>Assigns icons to a whole listing. Cheap because it is nearly all cache hits.</summary>
    public static void Populate(IReadOnlyList<FileSystemItem> items)
    {
        for (var i = 0; i < items.Count; i++)
            items[i].Icon = GetIcon(items[i]);
    }
}
