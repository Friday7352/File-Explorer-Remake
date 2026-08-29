using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Clearspace.Commands;
using Clearspace.Models;
using Clearspace.Native;
using Clearspace.Services;
using Clearspace.ViewModels;

namespace Clearspace;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private FileSystemItem? _renameTarget;
    private SidebarEntry? _sidebarDragEntry;
    private Button? _sidebarDragSource;
    private Point _sidebarDragStart;
    private string? _editingCategoryId;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // The view supplies the few behaviours actions cannot reach on their own.
        _viewModel.Context.SelectAll = () => FileList.SelectAll();
        _viewModel.Context.ClearSelection = () => FileList.UnselectAll();
        _viewModel.Context.InvertSelection = InvertSelection;
        _viewModel.Context.FocusAddressBar = ShowAddressEditor;
        _viewModel.Context.BeginRename = BeginRename;

        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseMove += OnSidebarMouseMove;
        PreviewMouseLeftButtonUp += OnSidebarMouseUp;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _viewModel.Context.OwnerHandle = handle;

        ApplyDarkTitleBar(handle);
        HookColumnHeaders();

        _viewModel.Start();
        FileList.Focus();
    }

    private static void ApplyDarkTitleBar(IntPtr handle)
    {
        var enabled = 1;
        // Newer builds use attribute 20; older ones used 19. Both are ignored when
        // unsupported, so setting each is safe.
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref enabled, sizeof(int));

        // Windows 11 rounds the window itself; Windows 10 ignores this and stays square.
        var round = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    // ---------- Key bindings ----------

    /// <summary>
    /// Every action declares its own chord, so this one handler is the entire
    /// keyboard layer. New actions get their shortcut with no change here.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Let text entry keep its own keys.
        if (Keyboard.FocusedElement is TextBox)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var command = _viewModel.Commands.TryGetByHotKey(new HotKey(key, Keyboard.Modifiers));

        if (command is null || !command.CanExecute(null))
            return;

        command.Execute(null);
        e.Handled = true;
    }

    // ---------- Navigation ----------

    private void OnSidebarClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SidebarEntry entry })
            return;

        if (string.IsNullOrEmpty(entry.Path))
            return;

        if (Directory.Exists(entry.Path) || entry.Path.StartsWith("clearspace://", StringComparison.OrdinalIgnoreCase))
        {
            _viewModel.Navigation.Navigate(entry.Path);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = entry.Path, UseShellExecute = true });
        }
        catch (Exception) { }
    }

    private void OnToggleSidebarSection(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: SidebarEntry entry } &&
            entry.IsSection && !string.IsNullOrWhiteSpace(entry.SectionId))
            _viewModel.ToggleSidebarSection(entry.SectionId);
    }

    /// <summary>
    /// Points a sidebar entry at a folder of the user's choosing. Known folders
    /// already resolve to their real location, so this is for the cases Windows
    /// does not model: a second Downloads folder, a project root, a network share.
    /// </summary>
    private void OnChangeSidebarLocation(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: SidebarEntry entry })
            return;

        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Choose a folder for {entry.Name}",
            InitialDirectory = System.IO.Directory.Exists(entry.Path) ? entry.Path : string.Empty,
            Multiselect = false
        };

        if (picker.ShowDialog(this) != true)
            return;

        _viewModel.SetSidebarLocation(entry.Name, picker.FolderName);
    }

    private void OnResetSidebarLocation(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SidebarEntry entry })
            _viewModel.ResetSidebarLocation(entry.Name);
    }

    private void OnUnpinSidebar(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SidebarEntry entry } && entry.IsPinned)
            _viewModel.UnpinDirectory(entry.Path);
    }

    private void OnPinDirectory(object sender, RoutedEventArgs e)
    {
        var selected = FileList.SelectedItems.Cast<FileSystemItem>().FirstOrDefault();
        if (selected is not null)
            _viewModel.PinDirectory(selected.FullPath);
    }

    // ---------- Sidebar categories and drag/drop ----------

    private void OnCreateCategory(object sender, RoutedEventArgs e) => BeginCategoryEdit(null, string.Empty);

    private void OnRenameCategory(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SidebarEntry entry } && entry.IsSection)
            BeginCategoryEdit(entry.SectionId, entry.Name);
    }

    private void OnOpenSectionHub(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SidebarEntry entry } && entry.HasHub)
            _viewModel.Navigation.Navigate(entry.Path);
    }

    private void OnDeleteCategory(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: SidebarEntry entry } &&
            entry.IsCategory && !string.IsNullOrWhiteSpace(entry.CategoryId))
            _viewModel.DeletePinnedCategory(entry.CategoryId);
    }

    private void BeginCategoryEdit(string? categoryId, string name)
    {
        _editingCategoryId = categoryId;
        CategoryPanelTitle.Text = categoryId is null ? "New category" : "Rename category";
        CategoryBox.Text = name;
        CategoryPanel.Visibility = Visibility.Visible;
        CategoryBox.Focus();
        CategoryBox.SelectAll();
    }

    private void OnCategoryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseCategoryPanel();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        var name = CategoryBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SystemSounds_Beep();
            return;
        }

        if (string.IsNullOrWhiteSpace(_editingCategoryId))
            _viewModel.CreatePinnedCategory(name);
        else
            _viewModel.RenameSidebarSection(_editingCategoryId, name);

        CloseCategoryPanel();
    }

    private void CloseCategoryPanel()
    {
        CategoryPanel.Visibility = Visibility.Collapsed;
        _editingCategoryId = null;
        FileList.Focus();
    }

    private void OnSidebarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { DataContext: SidebarEntry entry } && (entry.IsPinned || entry.IsSection))
        {
            _sidebarDragEntry = entry;
            _sidebarDragSource = (Button)sender;
            _sidebarDragStart = e.GetPosition(this);
        }
        else
        {
            _sidebarDragEntry = null;
            _sidebarDragSource = null;
        }
    }

    private void OnSidebarMouseMove(object sender, MouseEventArgs e)
    {
        if (_sidebarDragEntry is null || _sidebarDragSource is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _sidebarDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _sidebarDragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var source = _sidebarDragEntry;
        _sidebarDragEntry = null;
        var sourceButton = _sidebarDragSource;
        _sidebarDragSource = null;
        DragDrop.DoDragDrop(sourceButton, new DataObject(typeof(SidebarEntry), source), DragDropEffects.Move);
    }

    private void OnSidebarMouseUp(object sender, MouseButtonEventArgs e)
    {
        _sidebarDragEntry = null;
        _sidebarDragSource = null;
    }

    private void OnSidebarDragEnter(object sender, DragEventArgs e)
    {
        if (sender is Button button && TryGetSidebarDrop(e, button, out _))
            ShowSidebarDropTarget(button, e);
    }

    private void OnSidebarDragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button button)
            ClearSidebarDropTarget(button);
    }

    private void OnSidebarDragOver(object sender, DragEventArgs e)
    {
        if (sender is Button button && TryGetSidebarDrop(e, button, out _))
        {
            ShowSidebarDropTarget(button, e);
            e.Effects = DragDropEffects.Move;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnSidebarDrop(object sender, DragEventArgs e)
    {
        if (sender is not Button button || !TryGetSidebarDrop(e, button, out var source) ||
            button.DataContext is not SidebarEntry target)
            return;

        ClearSidebarDropTarget(button);
        var placeAfter = e.GetPosition(button).Y >= button.ActualHeight / 2;

        if (source.IsPinned)
        {
            if (target.IsCategory && !string.IsNullOrWhiteSpace(target.CategoryId))
                _viewModel.MovePinnedDirectory(source.Path, target.CategoryId, null, placeAfter);
            else if (target.IsPinned)
                _viewModel.MovePinnedDirectory(source.Path, target.CategoryId, target.Path, placeAfter);
            else if (target.IsPinnedRoot)
                _viewModel.MovePinnedDirectory(source.Path, null, null, placeAfter);
            else
                return;

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (source.IsSection && target.IsSection &&
            !string.IsNullOrWhiteSpace(source.SectionId) && !string.IsNullOrWhiteSpace(target.SectionId))
        {
            _viewModel.MoveSidebarSection(source.SectionId, target.SectionId, placeAfter);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private static bool TryGetSidebarDrop(DragEventArgs e, Button targetButton, out SidebarEntry source)
    {
        source = null!;
        if (targetButton.DataContext is not SidebarEntry target ||
            !e.Data.GetDataPresent(typeof(SidebarEntry)) ||
            e.Data.GetData(typeof(SidebarEntry)) is not SidebarEntry dragged)
            return false;

        if (dragged.IsPinned)
        {
            source = dragged;
            return target.IsPinned || target.IsPinnedRoot || target.IsCategory;
        }

        if (dragged.IsSection)
        {
            source = dragged;
            return target.IsSection && !string.Equals(dragged.SectionId, target.SectionId, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static void ShowSidebarDropTarget(Button button, DragEventArgs e)
    {
        var placeAfter = e.GetPosition(button).Y >= button.ActualHeight / 2;
        button.Background = new SolidColorBrush(Color.FromArgb(30, 211, 161, 95));
        button.BorderBrush = new SolidColorBrush(Color.FromRgb(211, 161, 95));
        button.BorderThickness = placeAfter ? new Thickness(0, 0, 0, 2) : new Thickness(0, 2, 0, 0);
    }

    private static void ClearSidebarDropTarget(Button button)
    {
        button.Background = Brushes.Transparent;
        button.BorderBrush = Brushes.Transparent;
        button.BorderThickness = new Thickness(0);
    }

    private void OnToggleLayout(object sender, RoutedEventArgs e) => _viewModel.ToggleLayout();

    private void OnZoomIn(object sender, RoutedEventArgs e) => _viewModel.AdjustTileScale(0.15);

    private void OnZoomOut(object sender, RoutedEventArgs e) => _viewModel.AdjustTileScale(-0.15);

    private void OnFileListMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_viewModel.IsGrid || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        _viewModel.AdjustTileScale(e.Delta > 0 ? 0.10 : -0.10);
        e.Handled = true;
    }

    /// <summary>
    /// Fires when a recycled tile is handed a new item, which is the moment that
    /// tile becomes visible. Requesting here means only on-screen files ever have
    /// a thumbnail extracted.
    /// </summary>
    private void OnTileDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is FileSystemItem item)
            ThumbnailService.Request(item, ThumbnailSize);
    }

    // An inherited DataContext can already be present by the time the template's
    // DataContextChanged handler is attached. Loaded guarantees the first item is
    // requested too; the service deduplicates it if both events fire.
    private void OnTileLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FileSystemItem item })
        {
            ThumbnailService.Request(item, ThumbnailSize);
        }
    }

    // This is the source resolution, not the on-screen size. Request enough
    // pixels for the largest supported zoom level so previews remain sharp.
    private const int ThumbnailSize = 512;

    private void OnBreadcrumbClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && !string.IsNullOrEmpty(path))
            _viewModel.Navigation.Navigate(path);
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Ignore double-clicks on the header or empty space below the rows.
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is null)
            return;

        _viewModel.OpenCommand.Execute(null);
    }

    private void OnFileListRightClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is not { DataContext: FileSystemItem item })
            return;

        if (!FileList.SelectedItems.Contains(item))
            FileList.SelectedItem = item;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewModel.Context.SelectedItems = FileList.SelectedItems
            .Cast<FileSystemItem>()
            .ToList();
    }

    private void InvertSelection()
    {
        var selected = FileList.SelectedItems.Cast<FileSystemItem>().ToHashSet();

        FileList.SelectedItems.Clear();
        foreach (var item in _viewModel.Items)
        {
            if (!selected.Contains(item))
                FileList.SelectedItems.Add(item);
        }
    }

    // ---------- Address bar ----------

    private void OnAddressActivate(object sender, MouseButtonEventArgs e) => ShowAddressEditor();

    private void ShowAddressEditor()
    {
        BreadcrumbBar.Visibility = Visibility.Collapsed;
        AddressActivator.Visibility = Visibility.Collapsed;
        AddressBox.Visibility = Visibility.Visible;
        AddressBox.Text = _viewModel.CurrentPath;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void HideAddressEditor()
    {
        AddressBox.Visibility = Visibility.Collapsed;
        BreadcrumbBar.Visibility = Visibility.Visible;
        AddressActivator.Visibility = Visibility.Visible;
    }

    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            var target = AddressBox.Text.Trim().Trim('"');

            if (Directory.Exists(target))
            {
                _viewModel.Navigation.Navigate(target);
                HideAddressEditor();
                FileList.Focus();
            }
            else if (File.Exists(target))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = target,
                        UseShellExecute = true
                    });
                }
                catch (Exception)
                {
                    // No association for this file type.
                }
            }
            else
            {
                SystemSounds_Beep();
            }

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HideAddressEditor();
            FileList.Focus();
            e.Handled = true;
        }
    }

    private void OnAddressLostFocus(object sender, RoutedEventArgs e) => HideAddressEditor();

    private static void SystemSounds_Beep() => System.Media.SystemSounds.Beep.Play();

    // ---------- Rename ----------

    private void BeginRename(FileSystemItem? item)
    {
        if (item is null)
            return;

        _renameTarget = item;
        RenameBox.Text = item.Name;
        RenamePanel.Visibility = Visibility.Visible;
        RenameBox.Focus();

        // Preselect the stem so the extension is easy to keep.
        var stemLength = item.IsFolder
            ? item.Name.Length
            : item.Name.Length - Path.GetExtension(item.Name).Length;

        RenameBox.Select(0, Math.Max(0, stemLength));
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelRename();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter)
            return;

        e.Handled = true;

        var item = _renameTarget;
        var newName = RenameBox.Text.Trim();

        if (item is null || string.IsNullOrEmpty(newName) || newName == item.Name)
        {
            CancelRename();
            return;
        }

        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            SystemSounds_Beep();
            return;
        }

        var directory = Path.GetDirectoryName(item.FullPath);
        if (directory is null)
        {
            CancelRename();
            return;
        }

        FileOperationService.Rename(item.FullPath, Path.Combine(directory, newName), _viewModel.Context.OwnerHandle);
        CancelRename();
        _ = _viewModel.RefreshAsync();
    }

    private void CancelRename()
    {
        RenamePanel.Visibility = Visibility.Collapsed;
        _renameTarget = null;
        FileList.Focus();
    }

    // ---------- Column sorting ----------

    private void HookColumnHeaders()
        => FileList.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(OnColumnHeaderClick));

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Content: string header })
            return;

        var column = header switch
        {
            "Date modified" => SortColumn.DateModified,
            "Type" => SortColumn.Type,
            "Size" => SortColumn.Size,
            _ => SortColumn.Name
        };

        _viewModel.Sort(column);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null and not T)
            source = VisualTreeHelper.GetParent(source);

        return source as T;
    }
}
