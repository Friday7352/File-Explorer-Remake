using System.IO;
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
        if (sender is Button { Tag: string path } && !string.IsNullOrEmpty(path))
            _viewModel.Navigation.Navigate(path);
    }

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
