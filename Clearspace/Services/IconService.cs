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
/// Clearspace intentionally uses type icons—even for executables and shortcuts—during
/// initial listing. Asking the shell for a per-file icon makes a Downloads folder with
/// thousands of installers perform thousands of synchronous shell calls and can stall
/// or crash the UI. A per-file icon can be fetched later for an explicit preview.
/// </summary>
public static class IconService
{
    private static readonly ConcurrentDictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ImageSource?> LargeIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> TypeNameCache = new(StringComparer.OrdinalIgnoreCase);

    private const string FolderKey = "\\__folder__";

    public static ImageSource? GetIcon(FileSystemItem item)
    {
        // A drive is navigable like a folder, but it should still read as a device.
        // Ask the shell for the real root icon rather than applying the folder type
        // icon we use for ordinary directories.
        if (item.IsDriveRoot)
            return IconCache.GetOrAdd($"drive:{item.FullPath}", _ => LoadIcon(item.FullPath, isFolder: true, useAttributes: false, large: true));

        if (item.IsFolder)
            return IconCache.GetOrAdd(FolderKey, _ => LoadIcon(item.FullPath, isFolder: true, useAttributes: true));

        var extension = item.Extension;

        // No extension means the shell has nothing to key off; treat it as generic.
        if (string.IsNullOrEmpty(extension))
            return IconCache.GetOrAdd(".__none__", _ => LoadIcon("file", isFolder: false, useAttributes: true));

        return IconCache.GetOrAdd(extension, ext => LoadIcon("file" + ext, isFolder: false, useAttributes: true));
    }

    public static string GetTypeName(FileSystemItem item)
    {
        if (item.IsDriveRoot)
            return item.DriveKind ?? "Drive";

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

    /// <summary>
    /// Returns the actual high-resolution icon registered with Windows for this
    /// path/type. Grid tiles use this instead of stretching a small details icon.
    /// </summary>
    public static ImageSource? GetLargeIcon(FileSystemItem item)
    {
        var key = item.IsFolder
            ? item.IsDriveRoot ? $"drive:{item.FullPath}" : FolderKey
            : string.IsNullOrEmpty(item.Extension) ? ".__none__" : item.Extension;

        return LargeIconCache.GetOrAdd(key, _ => LoadLargeIcon(item));
    }

    private static ImageSource? LoadLargeIcon(FileSystemItem item)
    {
        var info = new NativeMethods.SHFILEINFO();
        var result = NativeMethods.SHGetFileInfoW(
            item.FullPath,
            item.IsFolder ? NativeMethods.FILE_ATTRIBUTE_DIRECTORY : NativeMethods.FILE_ATTRIBUTE_NORMAL,
            ref info,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
            NativeMethods.SHGFI_SYSICONINDEX);

        if (result == IntPtr.Zero || info.iIcon < 0)
            return null;

        NativeMethods.IImageList? imageList = null;
        var icon = IntPtr.Zero;

        try
        {
            var iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
            if (NativeMethods.SHGetImageList(NativeMethods.SHIL_JUMBO, ref iid, out imageList) < 0 || imageList is null)
                return null;

            if (imageList.GetIcon(info.iIcon, 1, out icon) < 0 || icon == IntPtr.Zero)
                return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (icon != IntPtr.Zero)
                NativeMethods.DestroyIcon(icon);
            if (imageList is not null)
                System.Runtime.InteropServices.Marshal.ReleaseComObject(imageList);
        }
    }

    private static ImageSource? LoadIcon(string path, bool isFolder, bool useAttributes, bool large = false)
    {
        var info = new NativeMethods.SHFILEINFO();

        var flags = NativeMethods.SHGFI_ICON |
                    (large ? NativeMethods.SHGFI_LARGEICON : NativeMethods.SHGFI_SMALLICON);
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
