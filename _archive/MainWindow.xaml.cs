using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace Clearspace.Desktop;

public partial class MainWindow : Window
{
    private readonly List<string> history = [];
    private readonly List<string> networkLocations = [];
    private readonly List<FileEntry> allEntries = [];
    private List<string> clipboardPaths = [];
    private bool cutPending;
    private int historyIndex = -1;
    private int loadVersion;
    private CancellationTokenSource? folderLoadCancellation;
    private string currentPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clearspace", "wpf-settings.json");
    private const string ThisPcRoute = "clearspace://this-pc";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NormalizeGraphite(this);
        LoadNetworkLocations();
        BuildNavigation();
        NavigateTo(currentPath, true);
    }

    private static void NormalizeGraphite(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is Border border)
            {
                border.Background = Graphite(border.Background);
                border.BorderBrush = Graphite(border.BorderBrush);
            }
            if (child is Control control)
            {
                control.Background = Graphite(control.Background);
                control.Foreground = Graphite(control.Foreground);
                control.BorderBrush = Graphite(control.BorderBrush);
            }
            if (child is TextBlock text)
            {
                text.Background = Graphite(text.Background);
                text.Foreground = Graphite(text.Foreground);
            }
            NormalizeGraphite(child);
        }
    }

    private static Brush? Graphite(Brush? brush)
    {
        if (brush is not SolidColorBrush solid) return brush;
        var color = solid.Color;
        var replacement = color switch
        {
            { R: 39, G: 36, B: 45 } => Color.FromRgb(40, 41, 45),
            { R: 41, G: 38, B: 48 } => Color.FromRgb(42, 43, 47),
            { R: 44, G: 41, B: 50 } => Color.FromRgb(48, 49, 53),
            { R: 41, G: 37, B: 55 } => Color.FromRgb(42, 43, 47),
            { R: 64, G: 55, B: 90 } => Color.FromRgb(58, 60, 65),
            { R: 69, G: 64, B: 78 } => Color.FromRgb(70, 72, 77),
            _ => color
        };
        return replacement == color ? brush : new SolidColorBrush(replacement);
    }

    private void BuildNavigation()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var known = new[]
        {
            new NavigationLocation("⌂  Home", home),
            new NavigationLocation("▣  This PC", ThisPcRoute),
            new NavigationLocation("▣  Desktop", KnownFolder("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))),
            new NavigationLocation("▤  Documents", KnownFolder("Personal", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))),
            new NavigationLocation("⇩  Downloads", KnownFolder("{374DE290-123F-4565-9164-39C4925E467B}", Path.Combine(home, "Downloads"))),
            new NavigationLocation("▧  Pictures", KnownFolder("My Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)))
        }.Where(location => location.Path == ThisPcRoute || Directory.Exists(location.Path)).ToList();
        QuickNav.ItemsSource = known;
        QuickCards.ItemsSource = known.Where(location => location.Label is not "⌂  Home").Take(4).Select(location => location with { Detail = Describe(location.Path) }).ToList();
        var computer = DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive => new NavigationLocation($"◫  {drive.Name}  {drive.VolumeLabel}".Trim(), drive.RootDirectory.FullName)).ToList();
        computer.AddRange(networkLocations.Where(Directory.Exists).Select(path => new NavigationLocation($"◫  {path}", path)));
        ComputerNav.ItemsSource = computer;
        UpdateStorageInfo();
    }

    private static string KnownFolder(string name, string fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            return key?.GetValue(name) is string value ? Environment.ExpandEnvironmentVariables(value) : fallback;
        }
        catch { return fallback; }
    }

    private async void NavigateTo(string path, bool addHistory = true)
    {
        try
        {
            if (path == ThisPcRoute)
            {
                ShowThisPc(addHistory);
                return;
            }
            currentPath = Path.GetFullPath(path);
            if (!Directory.Exists(currentPath)) { SetStatus("That location is unavailable or requires permission."); return; }
            if (addHistory) { history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1); history.Add(currentPath); historyIndex = history.Count - 1; }
            LocationTitle.Text = Path.GetFileName(currentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : currentPath;
            Title = $"Clearspace — {currentPath}";
            var isHome = string.Equals(currentPath, homePath, StringComparison.OrdinalIgnoreCase);
            HomeSummary.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
            QuickAccessSection.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
            UpdateStorageInfo();
            folderLoadCancellation?.Cancel();
            folderLoadCancellation = new CancellationTokenSource();
            await ReadCurrentFolderAsync(folderLoadCancellation.Token);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { SetStatus(error.Message); }
    }

    private void ShowThisPc(bool addHistory)
    {
        folderLoadCancellation?.Cancel();
        loadVersion++;
        if (addHistory) { history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1); history.Add(ThisPcRoute); historyIndex = history.Count - 1; }
        LocationTitle.Text = "This PC";
        Title = "Clearspace — This PC";
        HomeSummary.Visibility = Visibility.Collapsed;
        QuickAccessSection.Visibility = Visibility.Collapsed;
        FilesHeading.Text = "Devices and drives";
        var drives = DriveInfo.GetDrives().Where(drive => drive.IsReady).Select(drive => new FileEntry(drive.RootDirectory.FullName, drive)).ToList();
        FileGrid.ItemsSource = drives;
        ItemCount.Text = $"{drives.Count:N0} drives";
        SetStatus("●  Drives are ready");
        UpdateStorageInfo();
    }

    private async Task ReadCurrentFolderAsync(CancellationToken cancellationToken = default)
    {
        var request = ++loadVersion;
        LoadingOverlay.Visibility = Visibility.Visible;
        FileGrid.Opacity = 0.45;
        SetStatus("●  Loading files…");
        try
        {
            var folder = currentPath;
            var entries = await Task.Run(() =>
            {
                var result = new List<FileEntry>();
                foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Add(new FileEntry(path));
                }
                return result.OrderBy(entry => !entry.IsFolder).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }, cancellationToken);
            if (request != loadVersion) return;
            allEntries.Clear();
            allEntries.AddRange(entries);
            ApplyFilter();
            FileGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
        }
        catch (UnauthorizedAccessException) { if (request == loadVersion) SetStatus("You do not have permission to view this folder."); }
        catch (IOException error) { if (request == loadVersion) SetStatus(error.Message); }
        catch (OperationCanceledException) { }
        finally
        {
            if (request == loadVersion)
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                FileGrid.Opacity = 1;
            }
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var entries = allEntries.Where(entry => string.IsNullOrWhiteSpace(query) || entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        FileGrid.ItemsSource = entries;
        ItemCount.Text = $"{entries.Count:N0} items";
        SetStatus(entries.Count == allEntries.Count ? "●  Everything is ready" : $"●  {entries.Count:N0} matching items");
    }

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs eventArgs)
    {
        if (sender is ListBox { SelectedItem: NavigationLocation location }) NavigateTo(location.Path);
    }
    private void QuickCard_Click(object sender, RoutedEventArgs eventArgs) { if (sender is Button { Tag: NavigationLocation location }) NavigateTo(location.Path); }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs eventArgs) => ApplyFilter();
    private void Back_Click(object sender, RoutedEventArgs eventArgs) { if (historyIndex > 0) NavigateTo(history[--historyIndex], false); }
    private void Forward_Click(object sender, RoutedEventArgs eventArgs) { if (historyIndex < history.Count - 1) NavigateTo(history[++historyIndex], false); }
    private void Up_Click(object sender, RoutedEventArgs eventArgs) { if (historyIndex >= 0 && history[historyIndex] == ThisPcRoute) return; var parent = Directory.GetParent(currentPath); if (parent is not null) NavigateTo(parent.FullName); }
    private void Sort_Click(object sender, RoutedEventArgs eventArgs) { allEntries.Reverse(); ApplyFilter(); SetStatus("●  Files sorted in reverse order"); }
    private void Details_Click(object sender, RoutedEventArgs eventArgs) => SetStatus("●  Clearspace is using its focused card view");

    private List<FileEntry> SelectedEntries() => FileGrid.SelectedItems.Cast<FileEntry>().ToList();
    private void FileGrid_DoubleClick(object sender, MouseButtonEventArgs eventArgs) => OpenSelected();
    private void FileGrid_RightClick(object sender, MouseButtonEventArgs eventArgs)
    {
        if (FindParent<ListBoxItem>(eventArgs.OriginalSource as DependencyObject)?.DataContext is FileEntry entry)
            FileGrid.SelectedItem = entry;
    }
    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null) { if (child is T typed) return typed; child = VisualTreeHelper.GetParent(child); }
        return null;
    }

    private void OpenSelected()
    {
        var selection = SelectedEntries(); if (selection.Count != 1) return;
        var entry = selection[0];
        if (entry.IsFolder) NavigateTo(entry.Path);
        else { try { Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true }); } catch (Exception error) { SetStatus(error.Message); } }
    }
    private void Open_Click(object sender, RoutedEventArgs eventArgs) => OpenSelected();
    private void OpenInExplorer_Click(object sender, RoutedEventArgs eventArgs)
    {
        var selection = SelectedEntries(); if (selection.Count != 1) return;
        var entry = selection[0];
        Process.Start(new ProcessStartInfo("explorer.exe", entry.IsFolder ? $"\"{entry.Path}\"" : $"/select,\"{entry.Path}\"") { UseShellExecute = true });
    }
    private void Copy_Click(object sender, RoutedEventArgs eventArgs) => CopySelection(false);
    private void Cut_Click(object sender, RoutedEventArgs eventArgs) => CopySelection(true);
    private void CopySelection(bool cut) { clipboardPaths = SelectedEntries().Select(entry => entry.Path).ToList(); cutPending = cut; SetStatus(clipboardPaths.Count == 0 ? "●  Select an item first" : $"●  {clipboardPaths.Count} item(s) ready to {(cut ? "move" : "copy")}"); }
    private void Paste_Click(object sender, RoutedEventArgs eventArgs) => Paste();
    private void Paste()
    {
        foreach (var source in clipboardPaths.ToList())
        {
            try { var destination = UniquePath(Path.Combine(currentPath, Path.GetFileName(source))); if (cutPending) MoveItem(source, destination); else CopyItem(source, destination); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { MessageBox.Show(error.Message, "Clearspace", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }
        if (cutPending) { clipboardPaths.Clear(); cutPending = false; }
        _ = ReadCurrentFolderAsync();
    }
    private void Rename_Click(object sender, RoutedEventArgs eventArgs)
    {
        var selection = SelectedEntries(); if (selection.Count != 1) return; var entry = selection[0];
        var name = Interaction.InputBox("Enter a new name:", "Clearspace — Rename", entry.Name).Trim(); if (name.Length == 0 || name == entry.Name) return;
        try { var target = Path.Combine(Path.GetDirectoryName(entry.Path)!, name); if (entry.IsFolder) Directory.Move(entry.Path, target); else File.Move(entry.Path, target); _ = ReadCurrentFolderAsync(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { MessageBox.Show(error.Message, "Could not rename", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private void Delete_Click(object sender, RoutedEventArgs eventArgs)
    {
        var selection = SelectedEntries(); if (selection.Count == 0 || MessageBox.Show($"Move {selection.Count} selected item(s) to the Recycle Bin?", "Clearspace", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var entry in selection) { if (entry.IsFolder) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(entry.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(entry.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin); }
        _ = ReadCurrentFolderAsync();
    }
    private void NewFolder_Click(object sender, RoutedEventArgs eventArgs)
    {
        var name = Interaction.InputBox("Folder name:", "Clearspace — New folder", "New folder").Trim(); if (name.Length == 0) return;
        Directory.CreateDirectory(UniquePath(Path.Combine(currentPath, name))); _ = ReadCurrentFolderAsync();
    }
    private void AddNetwork_Click(object sender, RoutedEventArgs eventArgs)
    {
        var dialog = new NetworkDriveDialog(DriveInfo.GetDrives().Where(drive => drive.IsReady)) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var path = dialog.Address;
        if (path.Length == 0) return;
        if (!path.StartsWith("\\\\") || !Directory.Exists(path)) { MessageBox.Show("Clearspace could not reach that shared folder. Check the address, connection, VPN, and permissions.", "Network location", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (!networkLocations.Contains(path, StringComparer.OrdinalIgnoreCase)) networkLocations.Add(path); SaveNetworkLocations(); BuildNavigation(); NavigateTo(path);
    }
    private void Window_KeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key == Key.Back && !SearchBox.IsFocused) Up_Click(sender, eventArgs);
        else if (Keyboard.Modifiers == ModifierKeys.Control && eventArgs.Key == Key.C) CopySelection(false);
        else if (Keyboard.Modifiers == ModifierKeys.Control && eventArgs.Key == Key.X) CopySelection(true);
        else if (Keyboard.Modifiers == ModifierKeys.Control && eventArgs.Key == Key.V) Paste();
        else if (eventArgs.Key == Key.F2) Rename_Click(sender, eventArgs);
        else if (eventArgs.Key == Key.Delete) Delete_Click(sender, eventArgs);
    }

    private void LoadNetworkLocations() { try { if (File.Exists(settingsPath)) networkLocations.AddRange(System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(settingsPath)) ?? []); } catch { } }
    private void SaveNetworkLocations() { Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!); File.WriteAllText(settingsPath, System.Text.Json.JsonSerializer.Serialize(networkLocations)); }
    private void SetStatus(string text) => StatusText.Text = text;
    private void UpdateStorageInfo()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(currentPath)!);
            if (!drive.IsReady) return;
            StorageBar.Value = Math.Round(100.0 * (drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize);
            StorageLabel.Text = $"{FormatSize(drive.AvailableFreeSpace)} free";
        }
        catch (IOException) { }
    }
    private static string Describe(string path) { try { return $"{Directory.EnumerateFileSystemEntries(path).Take(99).Count()} items"; } catch { return "Local"; } }
    private static void CopyItem(string source, string destination) { if (File.Exists(source)) File.Copy(source, destination); else CopyDirectory(source, destination); }
    private static void MoveItem(string source, string destination) { try { if (File.Exists(source)) File.Move(source, destination); else Directory.Move(source, destination); } catch (IOException) { CopyItem(source, destination); if (File.Exists(source)) File.Delete(source); else Directory.Delete(source, true); } }
    private static void CopyDirectory(string source, string destination) { Directory.CreateDirectory(destination); foreach (var dir in Directory.EnumerateDirectories(source, "*", System.IO.SearchOption.AllDirectories)) Directory.CreateDirectory(dir.Replace(source, destination, StringComparison.OrdinalIgnoreCase)); foreach (var file in Directory.EnumerateFiles(source, "*", System.IO.SearchOption.AllDirectories)) { var target = file.Replace(source, destination, StringComparison.OrdinalIgnoreCase); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target); } }
    private static string UniquePath(string candidate) { if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate; var parent = Path.GetDirectoryName(candidate)!; var name = Path.GetFileNameWithoutExtension(candidate); var ext = Path.GetExtension(candidate); for (var i = 2; ; i++) { var next = Path.Combine(parent, $"{name} ({i}){ext}"); if (!File.Exists(next) && !Directory.Exists(next)) return next; } }
    private static string FormatSize(long bytes) { string[] units = ["B", "KB", "MB", "GB", "TB"]; double value = bytes; var index = 0; while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; } return index == 0 ? $"{value:N0} {units[index]}" : $"{value:N1} {units[index]}"; }
    private sealed record NavigationLocation(string Label, string Path, string Detail = "Local");
    private sealed record FileEntry(string Path, DriveInfo? Drive = null)
    {
        public string Name => Drive is not null ? $"{Drive.Name}  {Drive.VolumeLabel}".Trim() : System.IO.Path.GetFileName(Path); public bool IsFolder => Directory.Exists(Path); public string Extension => System.IO.Path.GetExtension(Path); public long Size => IsFolder ? 0 : new FileInfo(Path).Length;
        public string TypeLabel => Drive is not null ? $"{Drive.DriveType} drive" : IsFolder ? "File folder" : Extension.Length > 1 ? $"{Extension[1..].ToUpperInvariant()} File" : "File";
        public string SizeLabel => Drive is not null ? $"{FormatSize(Drive.AvailableFreeSpace)} free of {FormatSize(Drive.TotalSize)}" : IsFolder ? "" : FormatSize(Size);
        public string ModifiedLabel => Drive is not null ? $"{Math.Round(100.0 * (Drive.TotalSize - Drive.AvailableFreeSpace) / Drive.TotalSize):N0}% used" : (IsFolder ? new DirectoryInfo(Path).LastWriteTime : new FileInfo(Path).LastWriteTime).ToString("g");
        public string Preview => Drive is not null ? "▰" : IsFolder ? "▰" : Extension.ToLowerInvariant() switch { ".pdf" => "PDF", ".doc" or ".docx" => "DOC", ".xls" or ".xlsx" or ".csv" => "XLS", ".jpg" or ".jpeg" or ".png" => "IMG", _ => Extension.Length > 1 ? Extension[1..].ToUpperInvariant() : "FILE" };
        public Visibility DriveVisibility => Drive is null ? Visibility.Collapsed : Visibility.Visible;
        public double UsagePercent { get => Drive is null || Drive.TotalSize == 0 ? 0 : Math.Round(100.0 * (Drive.TotalSize - Drive.AvailableFreeSpace) / Drive.TotalSize); set { } }
        public Brush PreviewBrush => Drive is not null || IsFolder ? Brushes.LightSteelBlue : Extension.ToLowerInvariant() switch { ".pdf" => Brushes.LightCoral, ".doc" or ".docx" => Brushes.LightSkyBlue, ".xls" or ".xlsx" or ".csv" => Brushes.MediumSeaGreen, ".jpg" or ".jpeg" or ".png" => Brushes.SandyBrown, _ => Brushes.LightGray };
        public string Meta => IsFolder ? "Folder" : $"{Preview} · {FormatSize(Size)}";
    }
}
