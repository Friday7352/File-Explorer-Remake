using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.IO;

namespace Clearspace.Desktop;

internal sealed class NetworkDriveDialog : Window
{
    private readonly TextBox _address;
    public string Address => _address.Text.Trim().TrimEnd('\\', '/');

    public NetworkDriveDialog(IEnumerable<DriveInfo> mappedDrives)
    {
        Title = "Add network drive";
        Width = 520;
        Height = 355;
        MinWidth = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new SolidColorBrush(Color.FromRgb(32, 33, 36));
        Foreground = Brushes.White;

        var root = new Grid { Margin = new Thickness(28) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        root.Children.Add(new TextBlock { Text = "Add a network drive", FontFamily = new FontFamily("Segoe UI Variable Display"), FontWeight = FontWeights.SemiBold, FontSize = 22 });
        var hint = new TextBlock { Text = "Paste a shared-folder address. Clearspace will verify it before adding it.", Foreground = new SolidColorBrush(Color.FromRgb(170, 172, 176)), Margin = new Thickness(0, 8, 0, 20), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(hint, 1); root.Children.Add(hint);

        _address = new TextBox { Text = "\\\\", FontSize = 14, Padding = new Thickness(12, 9, 12, 9), Background = new SolidColorBrush(Color.FromRgb(43, 44, 48)), Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(75, 77, 83)), BorderThickness = new Thickness(1) };
        Grid.SetRow(_address, 2); root.Children.Add(_address);

        var known = mappedDrives.Where(drive => drive.DriveType == DriveType.Network).ToList();
        var suggested = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        if (known.Count > 0)
        {
            suggested.Children.Add(new TextBlock { Text = "Already connected", Foreground = new SolidColorBrush(Color.FromRgb(170, 172, 176)), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 7) });
            foreach (var drive in known)
            {
                var option = new Button { Content = $"{drive.Name}  {drive.VolumeLabel}", HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 1, 0, 1), Background = new SolidColorBrush(Color.FromRgb(43, 44, 48)) };
                option.Click += (_, _) => _address.Text = drive.RootDirectory.FullName;
                suggested.Children.Add(option);
            }
        }
        Grid.SetRow(suggested, 3); root.Children.Add(suggested);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(0, 0, 8, 0) }; cancel.Click += (_, _) => DialogResult = false;
        var add = new Button { Content = "Add drive", Background = new SolidColorBrush(Color.FromRgb(82, 85, 91)), Padding = new Thickness(15, 8, 15, 8) }; add.Click += (_, _) => DialogResult = true;
        actions.Children.Add(cancel); actions.Children.Add(add); Grid.SetRow(actions, 4); root.Children.Add(actions);
        Loaded += (_, _) => _address.Focus();
    }
}
