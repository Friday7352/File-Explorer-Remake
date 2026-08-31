using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clearspace.Models;
using Clearspace.ViewModels;

namespace Clearspace.Services;

/// <summary>
/// A small, deliberately fictional workspace for product screenshots. None of
/// its paths map to the file system, so it is safe to show on a public README.
/// </summary>
internal static class DemoWorkspace
{
    internal const string Prefix = "clearspace://demo/";
    internal const string HomePath = Prefix + "home";
    internal const string DesktopPath = Prefix + "desktop";
    internal const string DocumentsPath = Prefix + "documents";
    internal const string DownloadsPath = Prefix + "downloads";
    internal const string PicturesPath = Prefix + "pictures";
    internal const string MusicPath = Prefix + "music";
    internal const string VideosPath = Prefix + "videos";
    internal const string FavoritesPath = Prefix + "favorites";
    internal const string StudioPath = Prefix + "studio";
    internal const string ArchivePath = Prefix + "archive";

    private static readonly ImageSource FolderImage = CreateFolderImage();
    private static readonly ImageSource DriveImage = CreateDriveImage();
    private static readonly string DemoAssetsPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Demo");

    internal static bool IsDemoPath(string path)
        => path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<SidebarEntry> Sidebar { get; } =
    [
        Header("Your files", MainViewModel.YourFilesPath, "files"),
        Child("Desktop", DesktopPath),
        Child("Documents", DocumentsPath),
        Child("Downloads", DownloadsPath),
        Child("Pictures", PicturesPath),
        Child("Music", MusicPath),
        Child("Videos", VideosPath),
        Header("Favorites", FavoritesPath, "favorites", favorites: true),
        Pin("Studio Notes", StudioPath),
        Pin("Design Archive", ArchivePath),
        Header("This PC", MainViewModel.MyPcPath, "this-pc"),
        Child("Local Disk (C:)", Prefix + "drive/c"),
        Child("Studio Drive (D:)", Prefix + "drive/d"),
        Child("Archive (E:)", Prefix + "drive/e"),
        Header("Network", MainViewModel.NetworkPath, "network"),
        new SidebarEntry("Team Share (S:)", Prefix + "network/team", IsNetworkDrive: true, IsChild: true),
        new SidebarEntry("Media Vault (P:)", Prefix + "network/media", IsNetworkDrive: true, IsChild: true)
    ];

    internal static DemoView ViewFor(string path)
    {
        var now = new DateTime(2026, 8, 30, 10, 24, 0);

        return path.ToLowerInvariant() switch
        {
            MainViewModel.MyPcPath => Drives(false),
            MainViewModel.NetworkPath => Drives(true),
            HomePath or MainViewModel.YourFilesPath => new DemoView(
                "Welcome to Clearspace",
                "Demo workspace",
                "A fictional workspace for README screenshots. Nothing here is read from this computer.",
                LayoutMode.Grid,
                DirectoryViewProfile.General,
                [Folder("Studio Notes", StudioPath, now), Folder("Design Archive", ArchivePath, now.AddDays(-1)),
                 Folder("Photography", PicturesPath, now.AddDays(-3)), Folder("Release Assets", DownloadsPath, now.AddDays(-5)),
                 File("Launch plan.docx", Prefix + "files/launch-plan.docx", ".docx", 1_482_000, now.AddHours(-2)),
                 File("Brand guidelines.pdf", Prefix + "files/brand-guidelines.pdf", ".pdf", 3_420_000, now.AddDays(-2)),
                 File("Aurora.png", Prefix + "files/aurora.png", ".png", 2_840_000, now.AddDays(-4))]),
            FavoritesPath => new DemoView(
                "Favorites", "2 saved locations", "Useful places you have pinned for quick access.", LayoutMode.Grid, DirectoryViewProfile.General,
                [Folder("Studio Notes", StudioPath, now), Folder("Design Archive", ArchivePath, now.AddDays(-1))]),
            DesktopPath => new DemoView(
                "Desktop", "8 items", "A clean example desktop with a mix of current work and shortcuts.", LayoutMode.Details, DirectoryViewProfile.Desktop,
                [Folder("Sprint planning", Prefix + "desktop/sprint-planning", now), Folder("Reference", Prefix + "desktop/reference", now),
                 File("Clearspace roadmap.pdf", Prefix + "desktop/roadmap.pdf", ".pdf", 924_000, now.AddHours(-1)),
                 File("Release checklist.docx", Prefix + "desktop/checklist.docx", ".docx", 142_000, now.AddDays(-1)),
                 File("Product teaser.mp4", Prefix + "desktop/teaser.mp4", ".mp4", 24_860_000, now.AddDays(-2))]),
            DocumentsPath => new DemoView(
                "Documents", "7 items", "Plans, notes, and references arranged for a project team.", LayoutMode.Details, DirectoryViewProfile.Documents,
                [Folder("Meeting notes", Prefix + "documents/meeting-notes", now), Folder("Research", Prefix + "documents/research", now.AddDays(-1)),
                 File("Q3 overview.docx", Prefix + "documents/q3-overview.docx", ".docx", 328_000, now),
                 File("Research summary.pdf", Prefix + "documents/research-summary.pdf", ".pdf", 1_920_000, now.AddDays(-2)),
                 File("Project budget.xlsx", Prefix + "documents/project-budget.xlsx", ".xlsx", 58_000, now.AddDays(-4))]),
            DownloadsPath => new DemoView(
                "Downloads", "6 items", "Recent downloads ready to sort, move, or share.", LayoutMode.Details, DirectoryViewProfile.Downloads,
                [Folder("Installers", Prefix + "downloads/installers", now), File("Clearspace-setup.exe", Prefix + "downloads/clearspace-setup.exe", ".exe", 46_200_000, now),
                 File("Demo-assets.zip", Prefix + "downloads/demo-assets.zip", ".zip", 12_820_000, now.AddDays(-1)),
                 File("Reference-photo.jpg", Prefix + "downloads/reference-photo.jpg", ".jpg", 4_880_000, now.AddDays(-2))]),
            PicturesPath => new DemoView(
                "Pictures", "12 items", "A visual workspace with crisp previews and folders that stay easy to scan.", LayoutMode.Grid, DirectoryViewProfile.Photos,
                [Folder("Campaign selects", Prefix + "pictures/campaign-selects", now), Folder("Product shots", Prefix + "pictures/product-shots", now.AddDays(-1)),
                 Folder("Moodboards", Prefix + "pictures/moodboards", now.AddDays(-3)), DemoImage("Lake cabin.png", "lake-cabin.png", now),
                 DemoImage("Glass forms.png", "glass-forms.png", now.AddDays(-1)), DemoImage("Creative desk.png", "creative-desk.png", now.AddDays(-2)),
                 File("Contact sheet.pdf", Prefix + "pictures/contact-sheet.pdf", ".pdf", 1_680_000, now.AddDays(-4)), File("Read me.txt", Prefix + "pictures/read-me.txt", ".txt", 2_400, now.AddDays(-5))]),
            MusicPath => new DemoView(
                "Music", "6 items", "Albums and tracks with a focused music view.", LayoutMode.Details, DirectoryViewProfile.Music,
                [Folder("Late-night sessions", Prefix + "music/late-night-sessions", now), File("Aurora Drive.flac", Prefix + "music/aurora-drive.flac", ".flac", 42_600_000, now),
                 File("First Light.mp3", Prefix + "music/first-light.mp3", ".mp3", 9_800_000, now.AddDays(-1)), File("Northern Line.mp3", Prefix + "music/northern-line.mp3", ".mp3", 8_900_000, now.AddDays(-2))]),
            VideosPath => new DemoView(
                "Videos", "7 items", "Video projects, edits, and exported clips in one place.", LayoutMode.Grid, DirectoryViewProfile.Videos,
                [Folder("Launch edits", Prefix + "videos/launch-edits", now), Folder("Screen recordings", Prefix + "videos/screen-recordings", now.AddDays(-2)),
                 File("Clearspace overview.mp4", Prefix + "videos/overview.mp4", ".mp4", 84_600_000, now), File("Design reel.mkv", Prefix + "videos/design-reel.mkv", ".mkv", 142_000_000, now.AddDays(-1)),
                 File("Motion study.mp4", Prefix + "videos/motion-study.mp4", ".mp4", 57_800_000, now.AddDays(-3))]),
            StudioPath => new DemoView(
                "Studio Notes", "5 items", "A fictional project directory for product screenshots.", LayoutMode.Details, DirectoryViewProfile.General,
                [Folder("References", Prefix + "studio/references", now), Folder("Exports", Prefix + "studio/exports", now.AddDays(-1)),
                 File("Notes.md", Prefix + "studio/notes.md", ".md", 12_000, now), File("Tasks.csv", Prefix + "studio/tasks.csv", ".csv", 9_000, now.AddHours(-6))]),
            ArchivePath => new DemoView(
                "Design Archive", "6 items", "A neat archive of source files, references, and completed work.", LayoutMode.Grid, DirectoryViewProfile.Photos,
                [Folder("2026 concepts", Prefix + "archive/2026-concepts", now), Folder("Brand system", Prefix + "archive/brand-system", now.AddDays(-2)),
                 File("grid-study.png", Prefix + "archive/grid-study.png", ".png", 2_800_000, now), File("type-samples.png", Prefix + "archive/type-samples.png", ".png", 3_100_000, now.AddDays(-2))]),
            _ when path.StartsWith(Prefix + "drive/", StringComparison.OrdinalIgnoreCase) => DriveContents(path, now),
            _ when path.StartsWith(Prefix + "network/", StringComparison.OrdinalIgnoreCase) => new DemoView(
                path.EndsWith("team", StringComparison.OrdinalIgnoreCase) ? "Team Share" : "Media Vault", "Shared location", "A fictional network location for the Clearspace demo.", LayoutMode.Details, DirectoryViewProfile.General,
                [Folder("Shared projects", path + "/projects", now), Folder("Incoming", path + "/incoming", now.AddDays(-1)), File("Welcome.txt", path + "/welcome.txt", ".txt", 2_400, now)]),
            _ => new DemoView("Demo workspace", "Sample location", "A fictional Clearspace location for screenshots.", LayoutMode.Grid, DirectoryViewProfile.General,
                [Folder("Example folder", Prefix + "example", now), File("Read me.txt", Prefix + "read-me.txt", ".txt", 1_200, now)])
        };
    }

    internal static IReadOnlyList<Breadcrumb> BreadcrumbsFor(string path)
    {
        var view = ViewFor(path);
        if (path.Equals(HomePath, StringComparison.OrdinalIgnoreCase) || path.Equals(MainViewModel.YourFilesPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("Demo workspace", HomePath)];
        if (path.Equals(MainViewModel.MyPcPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("This PC", MainViewModel.MyPcPath)];
        if (path.Equals(MainViewModel.NetworkPath, StringComparison.OrdinalIgnoreCase))
            return [new Breadcrumb("Network", MainViewModel.NetworkPath)];
        return [new Breadcrumb("Demo workspace", HomePath), new Breadcrumb(view.Title, path)];
    }

    internal static string AddressFor(string path) => ViewFor(path).Title;

    private static DemoView Drives(bool networkOnly)
    {
        var drives = networkOnly
            ? new[] { Drive("Team Share (S:)", Prefix + "network/team", "Network drive", 2_000L * Gb, 1_420L * Gb), Drive("Media Vault (P:)", Prefix + "network/media", "Network drive", 4_000L * Gb, 2_780L * Gb) }
            : new[] { Drive("Local Disk (C:)", Prefix + "drive/c", "Fixed drive", 1_000L * Gb, 412L * Gb), Drive("Studio Drive (D:)", Prefix + "drive/d", "Fixed drive", 2_000L * Gb, 1_320L * Gb), Drive("Archive (E:)", Prefix + "drive/e", "Fixed drive", 4_000L * Gb, 2_870L * Gb) };
        var total = drives.Sum(drive => drive.DriveTotalSpace);
        var available = drives.Sum(drive => drive.DriveAvailableSpace);
        return new DemoView(networkOnly ? "Network" : "This PC", $"{FileSystemItem.FormatSize(available)} available of {FileSystemItem.FormatSize(total)} total",
            networkOnly ? "Fictional shared locations in this screenshot-safe workspace." : "Three local drives with clear capacity indicators at a glance.", LayoutMode.Grid, DirectoryViewProfile.General, drives);
    }

    private static DemoView DriveContents(string path, DateTime now)
        => new(path.EndsWith("/c", StringComparison.OrdinalIgnoreCase) ? "Local Disk (C:)" : path.EndsWith("/d", StringComparison.OrdinalIgnoreCase) ? "Studio Drive (D:)" : "Archive (E:)",
            "Sample drive", "A fictional drive location in the Clearspace demo.", LayoutMode.Details, DirectoryViewProfile.General,
            [Folder("Projects", path + "/projects", now), Folder("Libraries", path + "/libraries", now.AddDays(-1)), Folder("Backups", path + "/backups", now.AddDays(-3)), File("Drive notes.txt", path + "/drive-notes.txt", ".txt", 4_600, now)]);

    private static readonly long Gb = 1024L * 1024L * 1024L;

    private static SidebarEntry Header(string name, string path, string id, bool favorites = false)
        => new(name, path, IsHeader: true, IsPinnedRoot: favorites, IsSection: true, SectionId: id);

    private static SidebarEntry Child(string name, string path)
        => new(name, path, IsKnownFolder: true, IsChild: true);

    private static SidebarEntry Pin(string name, string path)
        => new(name, path, IsPinned: true, IsChild: true);

    private static FileSystemItem Folder(string name, string path, DateTime modified)
        => new()
        {
            Name = name, FullPath = path, Attributes = FileAttributes.Directory, DateModified = modified, DateCreated = modified.AddDays(-30),
            TypeName = "File folder", Icon = FolderImage
        };

    private static FileSystemItem Drive(string name, string path, string kind, long total, long available)
        => new()
        {
            Name = name, FullPath = path, Attributes = FileAttributes.Directory, IsDriveRoot = true, DriveKind = kind,
            DriveTotalSpace = total, DriveAvailableSpace = available, TypeName = kind, Icon = DriveImage
        };

    private static FileSystemItem File(string name, string path, string extension, long size, DateTime modified)
    {
        var icon = MediaTypes.IsImage(extension)
            ? ScalableIconService.Image
            : MediaTypes.IsVideo(extension)
                ? ScalableIconService.Video
                : ScalableIconService.File(extension);
        return new FileSystemItem
        {
            Name = name, FullPath = path, Attributes = FileAttributes.Normal, Size = size, DateModified = modified, DateCreated = modified.AddDays(-7),
            TypeName = extension.TrimStart('.').ToUpperInvariant() + " file", Icon = icon
        };
    }

    private static FileSystemItem DemoImage(string name, string assetName, DateTime modified)
    {
        var path = Path.Combine(DemoAssetsPath, assetName);
        return new FileSystemItem
        {
            Name = name,
            FullPath = path,
            Attributes = FileAttributes.Normal,
            Size = System.IO.File.Exists(path) ? new FileInfo(path).Length : 0,
            DateModified = modified,
            DateCreated = modified.AddDays(-7),
            TypeName = "PNG image",
            Icon = ScalableIconService.Image,
            Thumbnail = LoadPreview(path)
        };
    }

    private static BitmapSource? LoadPreview(string path)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.DecodePixelWidth = 420;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            // A missing optional sample image falls back to the crisp image icon.
            return null;
        }
    }

    private static ImageSource CreateFolderImage()
    {
        var drawing = new DrawingGroup();
        var shadow = new SolidColorBrush(Color.FromArgb(48, 0, 0, 0)); shadow.Freeze();
        var edge = new SolidColorBrush(Color.FromRgb(207, 145, 29)); edge.Freeze();
        var back = new SolidColorBrush(Color.FromRgb(244, 183, 46)); back.Freeze();
        var front = new SolidColorBrush(Color.FromRgb(255, 213, 111)); front.Freeze();
        drawing.Children.Add(new GeometryDrawing(shadow, null, RoundedRect(new Rect(.10, .27, .82, .61), .065)));
        var tab = Geometry.Parse("M .12,.28 L .12,.16 L .46,.16 L .55,.28 Z"); tab.Freeze();
        drawing.Children.Add(new GeometryDrawing(back, new Pen(edge, .025), tab));
        drawing.Children.Add(new GeometryDrawing(front, new Pen(edge, .025), RoundedRect(new Rect(.08, .28, .84, .56), .065)));
        drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); return image;
    }

    private static ImageSource CreateDriveImage()
    {
        var drawing = new DrawingGroup();
        var caseBrush = new SolidColorBrush(Color.FromRgb(218, 220, 220)); caseBrush.Freeze();
        var dark = new SolidColorBrush(Color.FromRgb(59, 63, 64)); dark.Freeze();
        var indicator = new SolidColorBrush(Color.FromRgb(112, 198, 126)); indicator.Freeze();
        var outline = new SolidColorBrush(Color.FromRgb(153, 158, 157)); outline.Freeze();
        var top = Geometry.Parse("M .13,.38 L .28,.17 L .73,.17 L .88,.38 Z"); top.Freeze();
        drawing.Children.Add(new GeometryDrawing(caseBrush, new Pen(outline, .025), top));
        drawing.Children.Add(new GeometryDrawing(dark, new Pen(outline, .025), RoundedRect(new Rect(.13, .38, .75, .38), .035)));
        drawing.Children.Add(new GeometryDrawing(indicator, null, new EllipseGeometry(new Point(.26, .57), .045, .045)));
        drawing.Freeze(); var image = new DrawingImage(drawing); image.Freeze(); return image;
    }

    private static RectangleGeometry RoundedRect(Rect rect, double radius)
        => new(rect, radius, radius);

    internal sealed record DemoView(string Title, string Summary, string Description, LayoutMode Layout, DirectoryViewProfile Profile, IReadOnlyList<FileSystemItem> Items);
}
