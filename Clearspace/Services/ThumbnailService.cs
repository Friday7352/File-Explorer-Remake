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

    private static readonly BlockingCollection<ThumbnailRequest> Queue = new(new ConcurrentQueue<ThumbnailRequest>());
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, ImageSource> ShellIconCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Pending = new(StringComparer.OrdinalIgnoreCase);

    private static int _generation;
    private static Thread? _worker;
    private static readonly Lock StartLock = new();

    /// <summary>Invalidates every queued request. Called when the folder changes.</summary>
    public static void CancelPending() => Interlocked.Increment(ref _generation);

    public static void Request(FileSystemItem item, int size)
    {
        if (item.Thumbnail is not null)
            return;

        var key = CacheKey(item, size);

        if (Cache.TryGetValue(key, out var cached))
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

                Cache[request.CacheKey] = source;

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
                image.DecodePixelWidth = Math.Max(48, size * 2);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
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
            ShellIconCache.TryAdd(key, icon);
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
            var tab = new StreamGeometry();
            using (var geometry = tab.Open())
            {
                geometry.BeginFigure(new Point(width * .10, height * .29), true, true);
                geometry.LineTo(new Point(width * .39, height * .29), true, false);
                geometry.LineTo(new Point(width * .48, height * .16), true, false);
                geometry.LineTo(new Point(width * .70, height * .16), true, false);
                geometry.LineTo(new Point(width * .80, height * .29), true, false);
                geometry.LineTo(new Point(width * .90, height * .29), true, false);
                geometry.LineTo(new Point(width * .90, height * .78), true, false);
                geometry.LineTo(new Point(width * .10, height * .78), true, false);
            }
            tab.Freeze();
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(238, 183, 57)), null, tab);

            var previewBounds = new Rect(width * .14, height * .31, width * .72, height * .40);
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(68, 65, 58)), null, previewBounds, width * .035, width * .035);

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

            // The front lip gives the preview a recognisable Windows-folder shape
            // while the images remain visible in the open part of the folder.
            drawing.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(255, 214, 108)),
                null,
                new Rect(width * .10, height * .56, width * .80, height * .27),
                width * .045,
                width * .045);
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
            image.DecodePixelWidth = Math.Max(96, size);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
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
