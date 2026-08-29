using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;

namespace Clearspace;

public sealed class MainForm : Form
{
    private readonly TreeView navigation = new() { Dock = DockStyle.Fill, HideSelection = false, ShowLines = false, BorderStyle = BorderStyle.None };
    private readonly ListView files = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = true, BorderStyle = BorderStyle.None };
    private readonly FlowLayoutPanel cardGrid = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12, 4, 12, 18), WrapContents = true, BackColor = Color.FromArgb(28, 29, 32) };
    private readonly Panel contentHost = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(28, 29, 32) };
    private readonly TextBox address = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox filter = new() { Width = 250, PlaceholderText = "Search this folder" };
    private readonly Label locationTitle = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 20F), ForeColor = Color.FromArgb(247, 245, 251) };
    private readonly StatusStrip status = new();
    private readonly ToolStripStatusLabel statusText = new() { Text = "Ready" };
    private readonly ContextMenuStrip fileMenu = new();
    private readonly ContextMenuStrip navigationMenu = new();
    private readonly Dictionary<TreeNode, KnownFolder> knownFolderNodes = [];
    private readonly List<string> history = [];
    private readonly List<string> networkLocations = [];
    private readonly List<FileSystemItem> cardSelection = [];
    private readonly Dictionary<FileSystemItem, Panel> cards = [];
    private List<string> clipboardPaths = [];
    private bool gridView;
    private bool cutPending;
    private int historyIndex = -1;
    private string currentPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clearspace", "settings.json");

    public MainForm()
    {
        Text = "Clearspace";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        Size = new Size(1250, 790);
        Font = new Font("Segoe UI", 9F);
        BackColor = Color.FromArgb(28, 29, 32);
        KeyPreview = true;

        BuildLayout();
        ConfigureFilesList();
        ConfigureContextMenus();
        LoadNetworkLocations();
        BuildNavigation();
        SetViewMode(false);
        NavigateTo(currentPath, recordHistory: true);

        KeyDown += HandleKeyboardShortcut;
    }

    private void BuildLayout()
    {
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(16, 10, 16, 8), BackColor = Color.FromArgb(28, 29, 32), ForeColor = Color.FromArgb(226, 228, 232), Renderer = new DarkToolStripRenderer() };
        toolbar.Items.AddRange([
            Button("‹", (_, _) => GoBack()), Button("›", (_, _) => GoForward()), Button("↑", (_, _) => GoUp()),
            new ToolStripSeparator(), Button("Copy", (_, _) => CopySelection(false)), Button("Cut", (_, _) => CopySelection(true)), Button("Paste", (_, _) => PasteSelection()),
            new ToolStripSeparator(), Button("Rename", (_, _) => RenameSelection()), Button("Delete", (_, _) => DeleteSelection()), Button("New folder", (_, _) => CreateFolder()),
            new ToolStripSeparator(), Button("Grid", (_, _) => SetViewMode(true)), Button("Details", (_, _) => SetViewMode(false))
        ]);

        var addressPanel = new Panel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(12, 7, 12, 7), BackColor = Color.FromArgb(28, 29, 32) };
        var go = DarkButton("Go", 56);
        go.Dock = DockStyle.Right;
        go.Click += (_, _) => NavigateFromAddress();
        address.BackColor = Color.FromArgb(43, 45, 49);
        address.ForeColor = Color.FromArgb(239, 241, 244);
        address.BorderStyle = BorderStyle.FixedSingle;
        address.KeyDown += (_, eventArgs) => { if (eventArgs.KeyCode == Keys.Enter) NavigateFromAddress(); };
        addressPanel.Controls.Add(address);
        addressPanel.Controls.Add(go);

        var titlePanel = new Panel { Dock = DockStyle.Top, Height = 74, Padding = new Padding(16, 12, 16, 6), BackColor = Color.FromArgb(28, 29, 32) };
        var titleCaption = new Label { Text = "LOCATION", Dock = DockStyle.Top, Height = 17, ForeColor = Color.FromArgb(161, 166, 174), Font = new Font("Segoe UI Variable Text", 8F, FontStyle.Bold) };
        locationTitle.Dock = DockStyle.Top;
        titlePanel.Controls.Add(locationTitle);
        titlePanel.Controls.Add(titleCaption);

        var filterPanel = new Panel { Dock = DockStyle.Top, Height = 43, Padding = new Padding(12, 4, 12, 6), BackColor = Color.FromArgb(28, 29, 32) };
        var filterCaption = new Label { Text = "Files in this location", Dock = DockStyle.Left, Width = 132, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(176, 181, 188) };
        filter.BackColor = Color.FromArgb(43, 45, 49);
        filter.ForeColor = Color.FromArgb(239, 241, 244);
        filter.BorderStyle = BorderStyle.FixedSingle;
        filter.TextChanged += (_, _) => RefreshDirectory();
        filterPanel.Controls.Add(filter);
        filterPanel.Controls.Add(filterCaption);

        var splitter = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 250, BackColor = Color.FromArgb(60, 63, 69), FixedPanel = FixedPanel.Panel1 };
        splitter.Panel1.BackColor = Color.FromArgb(35, 37, 41);
        splitter.Panel1.Padding = new Padding(10, 0, 10, 8);
        var sideHeader = new Panel { Dock = DockStyle.Top, Height = 134, Padding = new Padding(18, 18, 18, 10), BackColor = Color.FromArgb(35, 37, 41) };
        var brand = new Label { Text = "✦  Clearspace", Dock = DockStyle.Top, Height = 36, ForeColor = Color.FromArgb(246, 247, 248), Font = new Font("Segoe UI Variable Display", 16F, FontStyle.Bold) };
        var tagline = new Label { Text = "Your files, clearly organized", Dock = DockStyle.Top, Height = 25, ForeColor = Color.FromArgb(170, 175, 183), Font = new Font("Segoe UI Variable Text", 9F) };
        var addNetwork = DarkButton("＋  Add network drive", 0);
        addNetwork.Dock = DockStyle.Bottom;
        addNetwork.Click += (_, _) => AddNetworkLocation();
        sideHeader.Controls.Add(addNetwork);
        sideHeader.Controls.Add(tagline);
        sideHeader.Controls.Add(brand);
        splitter.Panel1.Controls.Add(navigation);
        splitter.Panel1.Controls.Add(sideHeader);
        splitter.Panel2.BackColor = Color.FromArgb(28, 29, 32);
        splitter.Panel2.Padding = new Padding(12, 0, 12, 12);
        contentHost.Controls.Add(files);
        contentHost.Controls.Add(cardGrid);
        splitter.Panel2.Controls.Add(contentHost);
        splitter.Panel2.Controls.Add(filterPanel);
        splitter.Panel2.Controls.Add(titlePanel);
        splitter.Panel2.Controls.Add(addressPanel);

        status.Items.Add(statusText);
        status.SizingGrip = false;
        status.BackColor = Color.FromArgb(35, 37, 41);
        status.ForeColor = Color.FromArgb(176, 181, 188);
        Controls.Add(splitter);
        Controls.Add(toolbar);
        Controls.Add(status);
        status.Dock = DockStyle.Bottom;
        toolbar.Dock = DockStyle.Top;
    }

    private static ToolStripButton Button(string text, EventHandler action) => new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, ForeColor = Color.FromArgb(226, 228, 232) }.WithClick(action);

    private static Button DarkButton(string text, int width)
    {
        var button = new Button { Text = text, Width = width, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(55, 58, 64), ForeColor = Color.FromArgb(240, 242, 245), Font = new Font("Segoe UI Variable Text", 9F, FontStyle.Bold) };
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(71, 75, 82);
        return button;
    }

    private void ConfigureFilesList()
    {
        navigation.BackColor = Color.FromArgb(35, 37, 41);
        navigation.ForeColor = Color.FromArgb(225, 228, 232);
        navigation.LineColor = Color.FromArgb(60, 63, 69);
        navigation.Font = new Font("Segoe UI Variable Text", 10F);
        files.BackColor = Color.FromArgb(31, 33, 37);
        files.ForeColor = Color.FromArgb(239, 241, 244);
        files.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        files.OwnerDraw = true;
        files.DrawColumnHeader += DrawFileColumnHeader;
        files.DrawItem += (_, _) => { };
        files.DrawSubItem += DrawFileSubItem;
        files.Columns.Add("Name", 330);
        files.Columns.Add("Type", 145);
        files.Columns.Add("Size", 100, HorizontalAlignment.Right);
        files.Columns.Add("Date modified", 170);
        files.DoubleClick += (_, _) => OpenSelection();
        files.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.KeyCode == Keys.Enter) OpenSelection();
            if (eventArgs.KeyCode == Keys.F2) RenameSelection();
            if (eventArgs.KeyCode == Keys.Delete) DeleteSelection();
        };
        files.ColumnClick += (_, eventArgs) => SortByColumn(eventArgs.Column);
        cardGrid.ContextMenuStrip = fileMenu;
        cardGrid.MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button != MouseButtons.Right) return;
            cardSelection.Clear();
            RefreshCardSelection();
        };
    }

    private void DrawFileColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs eventArgs)
    {
        using var background = new SolidBrush(Color.FromArgb(43, 46, 51));
        eventArgs.Graphics.FillRectangle(background, eventArgs.Bounds);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            eventArgs.Header.Text,
            new Font("Segoe UI Variable Text", 8.5F, FontStyle.Bold),
            Rectangle.Inflate(eventArgs.Bounds, -12, 0),
            Color.FromArgb(185, 190, 198),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void DrawFileSubItem(object? sender, DrawListViewSubItemEventArgs eventArgs)
    {
        var selected = eventArgs.Item.Selected;
        using var background = new SolidBrush(selected ? Color.FromArgb(66, 70, 77) : Color.FromArgb(31, 33, 37));
        eventArgs.Graphics.FillRectangle(background, eventArgs.Bounds);
        var color = selected ? Color.White : Color.FromArgb(232, 235, 239);
        TextRenderer.DrawText(
            eventArgs.Graphics,
            eventArgs.SubItem.Text,
            new Font("Segoe UI Variable Text", 9F),
            Rectangle.Inflate(eventArgs.Bounds, -10, 0),
            color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void ConfigureContextMenus()
    {
        navigation.ContextMenuStrip = navigationMenu;
        navigation.NodeMouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Right) navigation.SelectedNode = eventArgs.Node;
        };
        var openLocation = new ToolStripMenuItem("Open", null, (_, _) =>
        {
            if (navigation.SelectedNode?.Tag is string path) NavigateTo(path);
        });
        var changeLocation = new ToolStripMenuItem("Change folder location…", null, (_, _) =>
        {
            if (navigation.SelectedNode is { } node && knownFolderNodes.TryGetValue(node, out var folder)) ChangeKnownFolderLocation(folder);
        });
        var openInExplorer = new ToolStripMenuItem("Open in Windows File Explorer", null, (_, _) => OpenCurrentInExplorer());
        navigationMenu.Items.AddRange([openLocation, new ToolStripSeparator(), changeLocation, openInExplorer]);
        navigationMenu.Opening += (_, eventArgs) =>
        {
            if (navigation.SelectedNode is null) { eventArgs.Cancel = true; return; }
            changeLocation.Visible = knownFolderNodes.ContainsKey(navigation.SelectedNode);
        };

        files.ContextMenuStrip = fileMenu;
        files.MouseDown += (_, eventArgs) =>
        {
            if (eventArgs.Button != MouseButtons.Right) return;
            var item = files.GetItemAt(eventArgs.X, eventArgs.Y);
            if (item is null) return;
            foreach (var selected in files.SelectedItems.Cast<ListViewItem>().ToList()) selected.Selected = false;
            item.Selected = true;
        };
        fileMenu.Items.AddRange([
            new ToolStripMenuItem("Open", null, (_, _) => OpenSelection()),
            new ToolStripMenuItem("Open in Windows File Explorer", null, (_, _) => OpenSelectionInExplorer()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Copy", null, (_, _) => CopySelection(false)),
            new ToolStripMenuItem("Cut", null, (_, _) => CopySelection(true)),
            new ToolStripMenuItem("Paste", null, (_, _) => PasteSelection()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("Rename", null, (_, _) => RenameSelection()),
            new ToolStripMenuItem("Delete", null, (_, _) => DeleteSelection()),
            new ToolStripSeparator(),
            new ToolStripMenuItem("New folder", null, (_, _) => CreateFolder()),
            new ToolStripMenuItem("Properties", null, (_, _) => ShowProperties())
        ]);
        fileMenu.Opening += (_, eventArgs) =>
        {
            var hasSelection = SelectedItems().Count > 0;
            foreach (ToolStripItem menuItem in fileMenu.Items)
            {
                if (menuItem.Text is "Open" or "Open in Windows File Explorer" or "Copy" or "Cut" or "Rename" or "Delete" or "Properties") menuItem.Enabled = hasSelection;
                if (menuItem.Text == "Paste") menuItem.Enabled = clipboardPaths.Count > 0;
            }
        };
    }

    private void BuildNavigation()
    {
        navigation.BeginUpdate();
        navigation.Nodes.Clear();
        knownFolderNodes.Clear();
        AddFolderNode("Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        foreach (var folder in KnownFolders)
        {
            var node = AddFolderNode(folder.Label, GetKnownFolderLocation(folder));
            if (node is not null) knownFolderNodes[node] = folder;
        }

        var drives = navigation.Nodes.Add("This PC");
        drives.NodeFont = new Font(Font, FontStyle.Bold);
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
            AddFolderNode($"{drive.Name}  {drive.VolumeLabel}".Trim(), drive.RootDirectory.FullName, drives.Nodes);

        var network = navigation.Nodes.Add("Network locations");
        network.NodeFont = new Font(Font, FontStyle.Bold);
        foreach (var location in networkLocations.Where(Directory.Exists))
            AddFolderNode(location, location, network.Nodes);

        navigation.AfterSelect -= NavigateFromTree;
        navigation.AfterSelect += NavigateFromTree;
        navigation.BeforeExpand -= ExpandTreeNode;
        navigation.BeforeExpand += ExpandTreeNode;
        navigation.EndUpdate();
    }

    private TreeNode? AddFolderNode(string label, string path, TreeNodeCollection? parent = null)
    {
        if (!Directory.Exists(path)) return null;
        var node = new TreeNode(label) { Tag = path };
        node.Nodes.Add(new TreeNode()); // Lazy-load child folders when expanded.
        (parent ?? navigation.Nodes).Add(node);
        return node;
    }

    private void ExpandTreeNode(object? sender, TreeViewCancelEventArgs eventArgs)
    {
        var node = eventArgs.Node;
        if (node is null || node.Tag is not string path || node.Nodes.Count != 1 || node.Nodes[0].Tag is not null) return;
        node.Nodes.Clear();
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(path).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                AddFolderNode(Path.GetFileName(directory), directory, node.Nodes);
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private void NavigateFromTree(object? sender, TreeViewEventArgs eventArgs)
    {
        if (eventArgs.Node?.Tag is string path) NavigateTo(path);
    }

    private void NavigateFromAddress()
    {
        var path = Environment.ExpandEnvironmentVariables(address.Text.Trim().Trim('"'));
        if (Directory.Exists(path)) NavigateTo(path);
        else SetStatus("That folder is unavailable or requires permission.");
    }

    private void NavigateTo(string path, bool recordHistory = true)
    {
        try
        {
            currentPath = Path.GetFullPath(path);
            address.Text = currentPath;
            Text = $"Clearspace — {currentPath}";
            locationTitle.Text = Path.GetFileName(currentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : currentPath;
            if (recordHistory)
            {
                history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
                history.Add(currentPath);
                historyIndex = history.Count - 1;
            }
            filter.Clear();
            RefreshDirectory();
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or ArgumentException)
        {
            SetStatus($"Could not open this folder: {error.Message}");
        }
    }

    private void RefreshDirectory()
    {
        if (!Directory.Exists(currentPath)) return;
        var query = filter.Text.Trim();
        files.BeginUpdate();
        files.Items.Clear();
        cardGrid.SuspendLayout();
        cardGrid.Controls.Clear();
        cards.Clear();
        cardSelection.Clear();
        try
        {
            var entries = Directory.EnumerateFileSystemEntries(currentPath)
                .Select(path => new FileSystemItem(path))
                .Where(item => string.IsNullOrWhiteSpace(query) || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => !item.IsDirectory)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (var entry in entries)
            {
                var row = new ListViewItem(entry.Name) { Tag = entry };
                row.SubItems.Add(entry.IsDirectory ? "File folder" : entry.Extension.Length > 1 ? entry.Extension[1..].ToUpperInvariant() + " File" : "File");
                row.SubItems.Add(entry.IsDirectory ? "" : FormatSize(entry.Size));
                row.SubItems.Add(entry.Modified.ToString("g"));
                files.Items.Add(row);
                if (gridView) AddFileCard(entry);
            }
            SetStatus($"{entries.Count:N0} item{(entries.Count == 1 ? "" : "s")}" + (query.Length > 0 ? " match this filter" : ""));
        }
        catch (UnauthorizedAccessException) { SetStatus("You do not have permission to view this folder."); }
        catch (IOException error) { SetStatus($"Could not read this folder: {error.Message}"); }
        finally { files.EndUpdate(); cardGrid.ResumeLayout(); }
    }

    private void OpenSelection()
    {
        var selection = SelectedItems();
        if (selection.Count != 1) return;
        var item = selection[0];
        if (item.IsDirectory) NavigateTo(item.Path);
        else
        {
            try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); }
            catch (Exception error) { SetStatus($"Windows could not open this file: {error.Message}"); }
        }
    }

    private List<FileSystemItem> SelectedItems() => gridView
        ? cardSelection.ToList()
        : files.SelectedItems.Cast<ListViewItem>().Select(item => (FileSystemItem)item.Tag!).ToList();

    private void AddFileCard(FileSystemItem item)
    {
        var card = new Panel
        {
            Size = new Size(194, 164),
            Margin = new Padding(7),
            Padding = new Padding(13),
            BackColor = Color.FromArgb(42, 39, 50),
            Cursor = Cursors.Hand,
            Tag = item
        };
        var preview = new Label
        {
            Text = item.IsDirectory ? "▰" : PreviewText(item.Extension),
            Dock = DockStyle.Top,
            Height = 76,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", item.IsDirectory ? 34F : 18F, FontStyle.Bold),
            ForeColor = item.IsDirectory ? Color.FromArgb(166, 146, 255) : PreviewColor(item.Extension),
            BackColor = Color.FromArgb(49, 45, 59)
        };
        var name = new Label
        {
            Text = item.Name,
            Dock = DockStyle.Top,
            Height = 31,
            Padding = new Padding(1, 8, 1, 0),
            AutoEllipsis = true,
            ForeColor = Color.FromArgb(241, 237, 247),
            Font = new Font("Segoe UI Semibold", 9F),
            BackColor = card.BackColor
        };
        var meta = new Label
        {
            Text = item.IsDirectory ? "Folder" : $"{(item.Extension.TrimStart('.').ToUpperInvariant() is { Length: > 0 } extension ? extension : "File")} · {FormatSize(item.Size)}",
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = Color.FromArgb(165, 158, 180),
            Font = new Font("Segoe UI", 8F),
            BackColor = card.BackColor
        };
        card.Controls.Add(meta);
        card.Controls.Add(name);
        card.Controls.Add(preview);
        AttachCardEvents(card, item);
        AttachCardEvents(preview, item);
        AttachCardEvents(name, item);
        AttachCardEvents(meta, item);
        card.ContextMenuStrip = fileMenu;
        preview.ContextMenuStrip = fileMenu;
        name.ContextMenuStrip = fileMenu;
        meta.ContextMenuStrip = fileMenu;
        cards[item] = card;
        cardGrid.Controls.Add(card);
    }

    private void AttachCardEvents(Control control, FileSystemItem item)
    {
        control.MouseDown += (_, eventArgs) =>
        {
            var toggle = ModifierKeys.HasFlag(Keys.Control);
            if (eventArgs.Button == MouseButtons.Right && cardSelection.Contains(item)) return;
            SelectCard(item, toggle);
        };
        control.DoubleClick += (_, _) =>
        {
            SelectCard(item, false);
            OpenSelection();
        };
    }

    private void SelectCard(FileSystemItem item, bool toggle)
    {
        if (!toggle) cardSelection.Clear();
        if (toggle && cardSelection.Contains(item)) cardSelection.Remove(item);
        else if (!cardSelection.Contains(item)) cardSelection.Add(item);
        RefreshCardSelection();
    }

    private void RefreshCardSelection()
    {
        foreach (var pair in cards)
        {
            var selected = cardSelection.Contains(pair.Key);
            pair.Value.BackColor = selected ? Color.FromArgb(72, 59, 109) : Color.FromArgb(42, 39, 50);
            foreach (Control child in pair.Value.Controls)
                if (child is not Label { Text: "▰" }) child.BackColor = pair.Value.BackColor;
        }
    }

    private void SetViewMode(bool useGrid)
    {
        gridView = useGrid;
        cardGrid.Visible = useGrid;
        files.Visible = !useGrid;
        if (useGrid) RefreshCardSelection();
        SetStatus(useGrid ? "Card view" : "Details view");
    }

    private static string PreviewText(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => "PDF", ".doc" or ".docx" => "DOC", ".xls" or ".xlsx" or ".csv" => "XLS", ".jpg" or ".jpeg" or ".png" or ".webp" => "IMG", _ => extension.Length > 1 ? extension[1..].ToUpperInvariant() : "FILE"
    };

    private static Color PreviewColor(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => Color.FromArgb(235, 126, 126), ".doc" or ".docx" => Color.FromArgb(133, 180, 240), ".xls" or ".xlsx" or ".csv" => Color.FromArgb(118, 210, 159), ".jpg" or ".jpeg" or ".png" or ".webp" => Color.FromArgb(247, 191, 121), _ => Color.FromArgb(184, 172, 207)
    };

    private void CopySelection(bool cut)
    {
        clipboardPaths = SelectedItems().Select(item => item.Path).ToList();
        cutPending = cut;
        SetStatus(clipboardPaths.Count == 0 ? "Select an item first." : $"{clipboardPaths.Count:N0} item(s) ready to { (cut ? "move" : "copy") }.");
    }

    private void PasteSelection()
    {
        if (clipboardPaths.Count == 0) { SetStatus("Nothing is ready to paste."); return; }
        var completed = 0;
        foreach (var source in clipboardPaths.ToList())
        {
            try
            {
                var destination = UniquePath(Path.Combine(currentPath, Path.GetFileName(source)));
                if (cutPending) MoveItem(source, destination); else CopyItem(source, destination);
                completed++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"Could not paste {Path.GetFileName(source)}.\n\n{error.Message}", "Clearspace", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        if (cutPending) { clipboardPaths.Clear(); cutPending = false; }
        RefreshDirectory();
        SetStatus($"Pasted {completed:N0} item(s).");
    }

    private static void CopyItem(string source, string destination)
    {
        if (File.Exists(source)) File.Copy(source, destination);
        else CopyDirectory(source, destination);
    }

    private static void MoveItem(string source, string destination)
    {
        try
        {
            if (File.Exists(source)) File.Move(source, destination);
            else Directory.Move(source, destination);
        }
        catch (IOException)
        {
            CopyItem(source, destination);
            if (File.Exists(source)) File.Delete(source); else Directory.Delete(source, true);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", System.IO.SearchOption.AllDirectories))
            Directory.CreateDirectory(directory.Replace(source, destination, StringComparison.OrdinalIgnoreCase));
        foreach (var file in Directory.EnumerateFiles(source, "*", System.IO.SearchOption.AllDirectories))
        {
            var target = file.Replace(source, destination, StringComparison.OrdinalIgnoreCase);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private void RenameSelection()
    {
        var selection = SelectedItems();
        if (selection.Count != 1) { SetStatus("Select one file or folder to rename."); return; }
        var item = selection[0];
        var newName = Interaction.InputBox("Enter a new name:", "Rename", item.Name, -1, -1).Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == item.Name) return;
        try
        {
            var destination = Path.Combine(Path.GetDirectoryName(item.Path)!, newName);
            if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("An item with that name already exists.");
            if (item.IsDirectory) Directory.Move(item.Path, destination); else File.Move(item.Path, destination);
            RefreshDirectory();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, error.Message, "Could not rename item", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void DeleteSelection()
    {
        var selection = SelectedItems();
        if (selection.Count == 0) { SetStatus("Select an item to delete."); return; }
        var message = $"Move {selection.Count:N0} selected item(s) to the Recycle Bin?";
        if (MessageBox.Show(this, message, "Clearspace", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        foreach (var item in selection)
        {
            try
            {
                if (item.IsDirectory) Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, error.Message, "Could not move item to Recycle Bin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        RefreshDirectory();
    }

    private void CreateFolder()
    {
        var name = Interaction.InputBox("Folder name:", "New folder", "New folder", -1, -1).Trim();
        if (string.IsNullOrWhiteSpace(name)) return;
        try { Directory.CreateDirectory(UniquePath(Path.Combine(currentPath, name))); RefreshDirectory(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { MessageBox.Show(this, error.Message, "Could not create folder", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private void AddNetworkLocation()
    {
        var path = Interaction.InputBox("Enter a shared-folder address.\nExample: \\server\team-files", "Add network location", "\\", -1, -1).Trim().TrimEnd('\\', '/');
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!path.StartsWith("\\\\") || !Directory.Exists(path))
        {
            MessageBox.Show(this, "Clearspace could not reach that shared folder. Check its address, connection, VPN, and permissions.", "Network location", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!networkLocations.Contains(path, StringComparer.OrdinalIgnoreCase)) networkLocations.Add(path);
        SaveNetworkLocations();
        BuildNavigation();
        NavigateTo(path);
    }

    private void ChangeKnownFolderLocation(KnownFolder folder)
    {
        var current = GetKnownFolderLocation(folder);
        using var picker = new FolderBrowserDialog
        {
            Description = $"Choose a new location for your Windows {folder.Label} folder",
            InitialDirectory = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseDescriptionForTitle = true
        };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        var target = picker.SelectedPath.TrimEnd('\\');
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase)) return;
        var choice = MessageBox.Show(this,
            $"Make this the Windows {folder.Label} location?\n\n{target}\n\nYes moves existing files there. No changes the location without moving files.",
            "Change folder location", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return;
        try
        {
            Directory.CreateDirectory(target);
            if (choice == DialogResult.Yes && Directory.Exists(current))
                MoveKnownFolderContents(current, target);
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", writable: true);
            key?.SetValue(folder.RegistryValueName, target, RegistryValueKind.ExpandString);
            BuildNavigation();
            NavigateTo(target);
            SetStatus($"Windows {folder.Label} now points to {target}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, error.Message, "Could not change folder location", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void MoveKnownFolderContents(string source, string target)
    {
        var sourceRoot = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var targetRoot = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (targetRoot.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The new location cannot be inside the current folder.");
        foreach (var item in Directory.EnumerateFileSystemEntries(source))
            MoveItem(item, UniquePath(Path.Combine(target, Path.GetFileName(item))));
    }

    private static string GetKnownFolderLocation(KnownFolder folder)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            var value = key?.GetValue(folder.RegistryValueName)?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) return Environment.ExpandEnvironmentVariables(value);
        }
        catch (Exception) { }
        return folder.FallbackPath();
    }

    private void OpenCurrentInExplorer()
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{currentPath}\"") { UseShellExecute = true }); }
        catch (Exception error) { SetStatus($"Could not open Windows File Explorer: {error.Message}"); }
    }

    private void OpenSelectionInExplorer()
    {
        var selection = SelectedItems();
        if (selection.Count != 1) return;
        var item = selection[0];
        try
        {
            var arguments = item.IsDirectory ? $"\"{item.Path}\"" : $"/select,\"{item.Path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception error) { SetStatus($"Could not open Windows File Explorer: {error.Message}"); }
    }

    private void ShowProperties()
    {
        var selection = SelectedItems();
        if (selection.Count != 1) return;
        var item = selection[0];
        var details = $"{item.Name}\n\nLocation: {item.Path}\nType: {(item.IsDirectory ? "File folder" : item.Extension + " file")}\nModified: {item.Modified:g}";
        if (!item.IsDirectory) details += $"\nSize: {FormatSize(item.Size)}";
        MessageBox.Show(this, details, "Properties", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void GoBack() { if (historyIndex > 0) NavigateTo(history[--historyIndex], false); }
    private void GoForward() { if (historyIndex < history.Count - 1) NavigateTo(history[++historyIndex], false); }
    private void GoUp() { var parent = Directory.GetParent(currentPath); if (parent is not null) NavigateTo(parent.FullName); }
    private void HandleKeyboardShortcut(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Alt && eventArgs.KeyCode == Keys.Left) GoBack();
        else if (eventArgs.Alt && eventArgs.KeyCode == Keys.Right) GoForward();
        else if (eventArgs.KeyCode == Keys.Back && !address.Focused && !filter.Focused) GoUp();
        else if (eventArgs.Control && eventArgs.KeyCode == Keys.C) CopySelection(false);
        else if (eventArgs.Control && eventArgs.KeyCode == Keys.X) CopySelection(true);
        else if (eventArgs.Control && eventArgs.KeyCode == Keys.V) PasteSelection();
    }

    private void SortByColumn(int column)
    {
        var rows = files.Items.Cast<ListViewItem>().ToList();
        rows.Sort((left, right) => string.Compare(left.SubItems[column].Text, right.SubItems[column].Text, StringComparison.OrdinalIgnoreCase));
        files.BeginUpdate(); files.Items.Clear(); files.Items.AddRange(rows.ToArray()); files.EndUpdate();
    }

    private void LoadNetworkLocations()
    {
        try
        {
            if (File.Exists(settingsPath))
                networkLocations.AddRange(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(settingsPath)) ?? []);
        }
        catch (Exception) { }
    }

    private void SaveNetworkLocations()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(networkLocations));
    }

    private void SetStatus(string message) => statusText.Text = message;

    private static readonly KnownFolder[] KnownFolders =
    [
        new("Desktop", "Desktop", () => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
        new("Documents", "Personal", () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
        new("Downloads", "{374DE290-123F-4565-9164-39C4925E467B}", () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
        new("Pictures", "My Pictures", () => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))
    ];

    private static string UniquePath(string candidate)
    {
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        var folder = Path.GetDirectoryName(candidate)!;
        var name = Path.GetFileNameWithoutExtension(candidate);
        var extension = Path.GetExtension(candidate);
        for (var suffix = 2; ; suffix++)
        {
            var proposed = Path.Combine(folder, $"{name} ({suffix}){extension}");
            if (!File.Exists(proposed) && !Directory.Exists(proposed)) return proposed;
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:N0} {units[unit]}" : $"{value:N1} {units[unit]}";
    }

    private sealed record FileSystemItem(string Path)
    {
        public string Name => System.IO.Path.GetFileName(Path);
        public bool IsDirectory => Directory.Exists(Path);
        public string Extension => System.IO.Path.GetExtension(Path);
        public long Size => IsDirectory ? 0 : new FileInfo(Path).Length;
        public DateTime Modified => IsDirectory ? new DirectoryInfo(Path).LastWriteTime : new FileInfo(Path).LastWriteTime;
    }

    private sealed record KnownFolder(string Label, string RegistryValueName, Func<string> FallbackPath);
}

internal static class ToolStripButtonExtensions
{
    public static ToolStripButton WithClick(this ToolStripButton button, EventHandler handler) { button.Click += handler; return button; }
}

internal sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(Color.FromArgb(31, 29, 38));
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs eventArgs)
    {
        if (eventArgs.Item.Selected)
        {
            using var brush = new SolidBrush(Color.FromArgb(58, 51, 75));
            eventArgs.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, eventArgs.Item.Size));
        }
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs eventArgs)
    {
        using var pen = new Pen(Color.FromArgb(68, 63, 79));
        var x = eventArgs.Item.Width / 2;
        eventArgs.Graphics.DrawLine(pen, x, 4, x, eventArgs.Item.Height - 4);
    }
}
