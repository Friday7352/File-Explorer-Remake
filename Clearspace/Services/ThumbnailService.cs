using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clearspace.Models;
using Clearspace.Native;

namespace Clearspace.Services;

/// <summary>
/// Produces real per-file thumbnails through IShellItemImageFactory, which is what
/// gives photo previews, video frames, and document first pages.
///
/// Two constraints shape this design. Shell thumbnail handlers are third-party COM
/// and expect an STA apartment, so requests run on one dedicated STA thread rather
/// than the thread pool. And extraction is slow enough that it must never block
/// navigation, so requests carry a generation number and stale ones are dropped
/// the moment the user moves to another folder.
/// </summary>
public static class ThumbnailService
{
    private sealed record ThumbnailRequest(FileSystemItem Item, int Size, int Generation, string CacheKey);

    private sealed class CacheEntry
    {
        public required string Key { get; init; }
        public required ImageSource Image { get; init; }
        public required long Bytes { get; init; }

        /// <summary>
        /// The item currently displaying this bitmap. Weak, so the cache never
        /// keeps a listing alive on its own.
        /// </summary>
        public required WeakReference<FileSystemItem> Owner { get; set; }
    }

    private static readonly BlockingCollection<ThumbnailRequest> Queue = new(new ConcurrentQueue<ThumbnailRequest>());

    // Decoded thumbnails are large and long-lived, so this is a hard-bounded LRU
    // rather than a plain dictionary. An unbounded cache here is the difference
    // between a flat 200 MB and multi-gigabyte growth over a browsing session.
    private const long MaxCacheBytes = 192L * 1024 * 1024;
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, LinkedListNode<CacheEntry>> CacheIndex = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<CacheEntry> CacheOrder = new();
    private static long _cacheBytes;

    private static readonly ConcurrentDictionary<string, ImageSource> ShellIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Current thumbnail cache size in bytes. Useful when profiling.</summary>
    internal static long CacheBytes => Interlocked.Read(ref _cacheBytes);

    private static bool TryGetCached(string key, FileSystemItem item, out ImageSource image)
    {
        lock (CacheGate)
        {
            if (CacheIndex.TryGetValue(key, out var node))
            {
                CacheOrder.Remove(node);
                CacheOrder.AddFirst(node);

                // A refresh builds new item objects for the same paths, so the
                // entry has to follow whichever one is on screen now.
                node.Value.Owner = new WeakReference<FileSystemItem>(item);

                image = node.Value.Image;
                return true;
            }
        }

        image = null!;
        return false;
    }

    private static void StoreCached(string key, ImageSource image, FileSystemItem owner)
    {
        var bytes = EstimateBytes(image);
        List<CacheEntry>? evicted = null;

        lock (CacheGate)
        {
            if (CacheIndex.Remove(key, out var existing))
            {
                CacheOrder.Remove(existing);
                _cacheBytes -= existing.Value.Bytes;
            }

            var node = CacheOrder.AddFirst(new CacheEntry
            {
                Key = key,
                Image = image,
                Bytes = bytes,
                Owner = new WeakReference<FileSystemItem>(owner)
            });

            CacheIndex[key] = node;
            _cacheBytes += bytes;

            while (_cacheBytes > MaxCacheBytes && CacheOrder.Last is { } oldest)
            {
                CacheOrder.RemoveLast();
                CacheIndex.Remove(oldest.Value.Key);
                _cacheBytes -= oldest.Value.Bytes;

                (evicted ??= []).Add(oldest.Value);
            }
        }

        ReleaseEvicted(evicted);
    }

    /// <summary>
    /// Detaches evicted bitmaps from the items still holding them.
    ///
    /// Without this the cache bound is meaningless: the dictionary shrinks while
    /// every item scrolled past keeps its own strong reference, so a folder of a
    /// few thousand photos still pins gigabytes. Off-screen tiles simply re-decode
    /// when scrolled back to, which is what a bounded viewer has to do.
    /// </summary>
    private static void ReleaseEvicted(List<CacheEntry>? evicted)
    {
        if (evicted is null || evicted.Count == 0)
            return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            () =>
            {
                foreach (var entry in evicted)
                {
                    // Only clear it if the item is still showing this exact bitmap;
                    // a newer thumbnail may have replaced it already.
                    if (entry.Owner.TryGetTarget(out var item) &&
                        ReferenceEquals(item.Thumbnail, entry.Image))
                        item.Thumbnail = null;
                }
            });
    }

    /// <summary>Pixel cost of a decoded image. Everything here ends up as 32bpp.</summary>
    private static long EstimateBytes(ImageSource image) => image switch
    {
        BitmapSource bitmap => (long)bitmap.PixelWidth * bitmap.PixelHeight * 4,
        // Vector placeholders are cheap and shared; treat them as negligible.
        _ => 4096
    };

    private static int _generation;
    private static Thread? _worker;
    private static readonly Lock StartLock = new();

    /// <summary>Invalidates every queued request. Called when the folder changes.</summary>
    public static void CancelPending() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// Forgets everything cached for one file, so the next request re-reads it from
    /// disk. Called after an edit writes the file back.
    ///
    /// The cache key carries the file's modified time, and the caller normally holds
    /// an item captured before the write, so its key no longer matches what a fresh
    /// listing would produce. Matching on the path prefix clears both.
    /// </summary>
    public static void Invalidate(string path)
    {
        var prefix = path + "|";

        lock (CacheGate)
        {
            var doomed = CacheIndex
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(pair => pair.Value)
                .ToList();

            foreach (var node in doomed)
            {
                CacheOrder.Remove(node);
                CacheIndex.Remove(node.Value.Key);
                _cacheBytes -= node.Value.Bytes;
            }
        }

        foreach (var key in Pending.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                Pending.TryRemove(key, out _);
        }

        var iconPrefix = $"path:{path}|";

        foreach (var key in ShellIconCache.Keys)
        {
            if (key.StartsWith(iconPrefix, StringComparison.OrdinalIgnoreCase))
                ShellIconCache.TryRemove(key, out _);
        }
    }

    public static void Request(FileSystemItem item, int size)
    {
        if (item.Thumbnail is not null)
            return;

        var key = CacheKey(item, size);

        if (TryGetCached(key, item, out var cached))
        {
            item.Thumbnail = cached;
            return;
        }

        // Recycled WPF tile containers can ask for the same image repeatedly while
        // scrolling. Keep exactly one decode in flight for each file and size.
        if (!Pending.TryAdd(key, 0))
            return;

        EnsureWorker();
        Queue.Add(new ThumbnailRequest(item, size, Volatile.Read(ref _generation), key));
    }

    private static void EnsureWorker()
    {
        if (_worker is not null)
            return;

        lock (StartLock)
        {
            if (_worker is not null)
                return;

            _worker = new Thread(Work)
            {
                IsBackground = true,
                Name = "Clearspace thumbnails",
                // Below normal so extraction never competes with the UI thread.
                Priority = ThreadPriority.BelowNormal
            };

            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }
    }

    private static void Work()
    {
        foreach (var request in Queue.GetConsumingEnumerable())
        {
            try
            {
                // The user has navigated since this was queued.
                if (request.Generation != Volatile.Read(ref _generation))
                    continue;

                var source = Extract(request.Item, request.Size);

                if (source is null)
                    continue;

                StoreCached(request.CacheKey, source, request.Item);

                if (request.Generation != Volatile.Read(ref _generation))
                    continue;

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null)
                    continue;

                dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    () => request.Item.Thumbnail = source);
            }
            finally
            {
                Pending.TryRemove(request.CacheKey, out _);
            }
        }
    }

    private static ImageSource? Extract(FileSystemItem item, int size)
    {
        var path = item.FullPath;

        // Explorer's folder tiles are not just an enlarged 16px folder glyph: when
        // useful content exists inside, they are a composed preview. Build the same
        // kind of high-resolution preview ourselves so it is crisp at any tile size.
        if (item.IsFolder && !item.IsDriveRoot && Directory.Exists(path))
        {
            // Preserve the real Windows folder artwork, then add a type badge.
            // This is requested at the tile's source size, so neither the folder
            // nor the mark becomes a stretched 16px list icon.
            if (FolderIconService.HasType(item))
            {
                var shellFolder = GetShellIcon(item, size) ?? IconService.GetLargeIcon(item);
                var typedFolder = FolderIconService.AddTypeBadge(item, shellFolder);
                if (typedFolder is not null)
                    return typedFolder;
            }

            var preview = CreateFolderPreview(path, size);
            if (preview is not null)
                return preview;
        }

        // Image files do not need a shell extension at all. Decoding directly is
        // faster and more dependable, and produces the actual pixels users expect.
        if (MediaTypes.IsImage(Path.GetExtension(path)))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path, UriKind.Absolute);
                // Decode at the requested size, not double it. ThumbnailSize is
                // already chosen to cover the largest tile zoom, so the old size * 2
                // cost four times the pixels for no visible gain: a 1024px decode is
                // about 3 MB of pixels per photo.
                image.DecodePixelWidth = Math.Max(48, size);
                image.CacheOption = BitmapCacheOption.OnLoad;
                // IgnoreImageCache matters after an edit. WPF keeps a process-wide
                // cache keyed on the URI, so without this a rotated file decodes
                // back to the bitmap from before the rotation.
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile |
                                      BitmapCreateOptions.IgnoreImageCache;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception)
            {
                // Corrupt or unsupported files fall back to the shell below.
            }
        }

        // Do not accept the registered app icon as a "thumbnail" for videos.
        // Decode an actual frame first so zooming has real image data to display.
        if (MediaTypes.IsVideo(Path.GetExtension(path)))
        {
            var videoFrame = VideoThumbnailService.Extract(path, size);
            if (videoFrame is not null)
                return videoFrame;
        }

        // Ask the same shell thumbnail providers that Explorer uses before ever
        // considering an associated-file icon. This is what returns a video frame
        // for .mp4/.mkv files and a real preview for formats with a registered
        // handler, rather than scaling a 32px application icon into a large tile.
        if (!item.IsFolder)
        {
            var shellThumbnail = ExtractShellImage(
                path,
                size,
                NativeMethods.SIIGBF_THUMBNAILONLY | NativeMethods.SIIGBF_BIGGERSIZEOK);

            if (shellThumbnail is not null)
                return shellThumbnail;
        }

        // This is Explorer's actual large-item path: ask the Shell item factory
        // for an icon composed at the tile's requested size. Do this before the
        // system image-list fallback, which may contain only a small bitmap.
        var shellIcon = GetShellIcon(item, size);
        if (shellIcon is not null)
            return shellIcon;

        // Only use our vector card when Windows provides neither a thumbnail nor
        // an exact-size Shell item image.
        if (!item.IsFolder)
            return MediaTypes.IsVideo(item.Extension)
                ? ScalableIconService.Video
                : ScalableIconService.File(item.Extension);

        // Some formats genuinely have no preview provider. In that case use the
        // shell's jumbo system image instead of the details-view (16/32px) icon.
        var largeIcon = IconService.GetLargeIcon(item);
        if (largeIcon is not null)
            return largeIcon;

        return null;
    }

    private static ImageSource? GetShellIcon(FileSystemItem item, int size)
    {
        // Association icons are identical for ordinary files with the same
        // extension. Executables, shortcuts, folders and drives can have unique
        // icons or overlays, so those remain keyed by path.
        var extension = item.Extension;
        var pathSpecific = item.IsFolder ||
                           extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                           extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                           extension.Equals(".ico", StringComparison.OrdinalIgnoreCase);
        var key = pathSpecific
            ? $"path:{item.FullPath}|{size}"
            : $"type:{(string.IsNullOrEmpty(extension) ? "file" : extension)}|{size}";

        if (ShellIconCache.TryGetValue(key, out var cached))
            return cached;

        var icon = ExtractShellImage(
            item.FullPath,
            size,
            NativeMethods.SIIGBF_ICONONLY | NativeMethods.SIIGBF_BIGGERSIZEOK);

        if (icon is not null)
        {
            // Path-keyed entries (executables, shortcuts, folders) grow with every
            // folder visited, unlike the extension-keyed ones which are naturally
            // few. Drop the lot rather than let it climb without limit; rebuilding
            // is a handful of cheap shell calls.
            if (ShellIconCache.Count > 2048)
                ShellIconCache.Clear();

            ShellIconCache.TryAdd(key, icon);
        }

        return icon;
    }

    private static ImageSource? ExtractShellImage(string path, int size, int flags)
    {
        var bitmap = IntPtr.Zero;

        try
        {
            var iid = NativeMethods.IID_IShellItemImageFactory;
            var result = NativeMethods.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory);

            if (result < 0 || factory is null)
                return null;

            try
            {
                var hr = factory.GetImage(new NativeMethods.SIZE(size, size), flags, out bitmap);
                if (hr < 0 || bitmap == IntPtr.Zero)
                    return null;

                var source = Imaging.CreateBitmapSourceFromHBitmap(
                    bitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());

                source.Freeze();
                return source;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(factory);
            }
        }
        catch (Exception)
        {
            // A broken third-party thumbnail handler must not take the app down.
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
                NativeMethods.DeleteObject(bitmap);
        }
    }

    private static string CacheKey(FileSystemItem item, int size)
        => $"{item.FullPath}|{size}|{item.DateModified.Ticks}";

    private static ImageSource? CreateFolderPreview(string path, int size)
    {
        var previewFiles = new List<string>(3);

        try
        {
            foreach (var candidate in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
            {
                if (!MediaTypes.IsImage(Path.GetExtension(candidate)))
                    continue;

                previewFiles.Add(candidate);
                if (previewFiles.Count == 3)
                    break;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var previews = previewFiles
            .Select(file => LoadImage(file, size))
            .Where(image => image is not null)
            .Cast<BitmapSource>()
            .ToList();

        // Keep a standard native folder for ordinary folders. A composed preview
        // only makes sense when it has real content to show.
        if (previews.Count == 0)
            return null;

        var pixels = Math.Max(128, size);
        var visual = new DrawingVisual();

        using (var drawing = visual.RenderOpen())
        {
            var width = (double)pixels;
            var height = (double)pixels;
            // Keep preview folders in the *same closed-folder silhouette* as every
            // other folder. The earlier composition looked like an open tray,
            // which made a content preview feel like a completely different icon.
            // The preview now sits neatly inside the front face instead.
            var back = new StreamGeometry();
            using (var geometry = back.Open())
            {
                geometry.BeginFigure(new Point(width * .10, height * .28), true, true);
                geometry.LineTo(new Point(width * .36, height * .28), true, false);
                geometry.LineTo(new Point(width * .45, height * .15), true, false);
                geometry.LineTo(new Point(width * .67, height * .15), true, false);
                geometry.LineTo(new Point(width * .77, height * .28), true, false);
                geometry.LineTo(new Point(width * .90, height * .28), true, false);
                geometry.LineTo(new Point(width * .90, height * .75), true, false);
                geometry.LineTo(new Point(width * .10, height * .75), true, false);
            }
            back.Freeze();
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(246, 184, 43)), null, back);

            var face = new Rect(width * .10, height * .34, width * .80, height * .43);
            drawing.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(255, 215, 111)),
                new Pen(new SolidColorBrush(Color.FromRgb(221, 158, 23)), Math.Max(1, width * .012)),
                face,
                width * .045,
                width * .045);

            // The image area is deliberately inset, like Explorer's folder
            // content previews, but it never changes the outer folder shape.
            var previewBounds = new Rect(width * .17, height * .44, width * .66, height * .22);
            drawing.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(68, 65, 58)),
                null,
                previewBounds,
                width * .018,
                width * .018);

            if (previews.Count == 1)
            {
                DrawCroppedImage(drawing, previews[0], previewBounds, width * .025);
            }
            else if (previews.Count > 1)
            {
                var gap = width * .025;
                var tileWidth = (previewBounds.Width - gap) / 2;
                DrawCroppedImage(drawing, previews[0], new Rect(previewBounds.X, previewBounds.Y, tileWidth, previewBounds.Height), width * .02);
                DrawCroppedImage(drawing, previews[1], new Rect(previewBounds.X + tileWidth + gap, previewBounds.Y, tileWidth, previewBounds.Height), width * .02);
            }

            // A light glaze makes the preview feel embedded in the same smooth
            // folder face, rather than appearing as an open-folder cavity.
            drawing.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
                new Pen(new SolidColorBrush(Color.FromArgb(76, 255, 255, 255)), Math.Max(1, width * .006)),
                previewBounds,
                width * .018,
                width * .018);
        }

        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource? LoadImage(string path, int size)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            // Folder previews draw these at roughly a third of the tile, so a
            // quarter-size decode is plenty and keeps three of them cheap.
            image.DecodePixelWidth = Math.Max(64, size / 3);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile |
                                  BitmapCreateOptions.IgnoreImageCache;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void DrawCroppedImage(DrawingContext drawing, BitmapSource image, Rect bounds, double radius)
    {
        var scale = Math.Max(bounds.Width / image.PixelWidth, bounds.Height / image.PixelHeight);
        var size = new Size(image.PixelWidth * scale, image.PixelHeight * scale);
        var destination = new Rect(
            bounds.X + (bounds.Width - size.Width) / 2,
            bounds.Y + (bounds.Height - size.Height) / 2,
            size.Width,
            size.Height);

        drawing.PushClip(new RectangleGeometry(bounds, radius, radius));
        drawing.DrawImage(image, destination);
        drawing.Pop();
    }
}
