namespace Clearspace.Shell;

internal sealed class ShellWindow : Form
{
    private const string ThisPc = "This PC";

    private readonly ShellBrowserHost _browser;
    private TextBox _addressBox = null!;
    private Label _statusLabel = null!;
    private Panel _content = null!;
    private Panel _thisPcOverview = null!;

    public ShellWindow()
    {
        Text = "Clearspace — Shell Preview";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 620);
        Size = new Size(1260, 820);
        BackColor = Color.FromArgb(246, 247, 248);
        Font = new Font("Segoe UI Variable Text", 9F, FontStyle.Regular, GraphicsUnit.Point);

        var titleBar = BuildHeader();
        Controls.Add(titleBar);

        var navigation = BuildNavigation();
        Controls.Add(navigation);

        _content = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(0, 1, 0, 0)
        };

        _browser = new ShellBrowserHost
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White
        };
        _browser.NavigationFailed += (_, target) => SetStatus($"Couldn’t open {target}");
        _browser.NavigationRequested += (_, target) =>
        {
            _addressBox.Text = target.StartsWith("shell:::", StringComparison.OrdinalIgnoreCase) ? FriendlyAddress(target) : target;
            SetStatus("Windows shell view");
        };
        _content.Controls.Add(_browser);
        _thisPcOverview = BuildThisPcOverview();
        _content.Controls.Add(_thisPcOverview);
        Controls.Add(_content);

        FormClosing += (_, eventArgs) =>
        {
            // A hosted shell view must never be able to close its containing application.
            // Normal title-bar and Alt+F4 closes remain available to the person using Clearspace.
            if (eventArgs.CloseReason is not CloseReason.UserClosing and not CloseReason.WindowsShutDown)
            {
                eventArgs.Cancel = true;
                SetStatus("The shell view was refreshed");
            }
        };

        Shown += (_, _) => BeginInvoke((MethodInvoker)(() =>
        {
            _browser.Start();
            Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var enabled = 1;
        // Windows 10/11 use different attribute numbers in older builds; both are harmless
        // when unsupported and ensure the native title bar does not flash bright white.
        ShellNative.DwmSetWindowAttribute(Handle, 20, ref enabled, sizeof(int));
        ShellNative.DwmSetWindowAttribute(Handle, 19, ref enabled, sizeof(int));
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 88,
            BackColor = Color.FromArgb(31, 33, 36),
            Padding = new Padding(18, 14, 18, 14)
        };

        var wordmark = new Label
        {
            AutoSize = true,
            Text = "✦  Clearspace",
            ForeColor = Color.FromArgb(245, 246, 247),
            Font = new Font("Segoe UI Variable Display", 13F, FontStyle.Bold),
            Location = new Point(18, 15)
        };
        header.Controls.Add(wordmark);

        _statusLabel = new Label
        {
            AutoSize = true,
            Text = "Windows shell baseline",
            ForeColor = Color.FromArgb(166, 171, 178),
            Font = new Font("Segoe UI Variable Text", 8.5F),
            Location = new Point(21, 44)
        };
        header.Controls.Add(_statusLabel);

        var tools = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 410,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 7, 0, 0)
        };

        tools.Controls.Add(CreateToolButton("‹", "Back", (_, _) => _browser.GoBack()));
        tools.Controls.Add(CreateToolButton("›", "Forward", (_, _) => _browser.GoForward()));
        tools.Controls.Add(CreateToolButton("↑", "Up", (_, _) => _browser.GoUp()));

        _addressBox = new TextBox
        {
            Width = 238,
            Height = 33,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(47, 50, 54),
            ForeColor = Color.FromArgb(239, 240, 242),
            Font = new Font("Segoe UI Variable Text", 9F),
            Text = "This PC",
            Margin = new Padding(10, 0, 6, 0)
        };
        _addressBox.KeyDown += AddressBoxKeyDown;
        tools.Controls.Add(_addressBox);
        tools.Controls.Add(CreateToolButton("Go", "Open address", (_, _) => Navigate(_addressBox.Text), 48));
        header.Controls.Add(tools);

        return header;
    }

    private Control BuildNavigation()
    {
        var sidebar = new Panel
        {
            Dock = DockStyle.Left,
            Width = 218,
            BackColor = Color.FromArgb(38, 40, 44),
            Padding = new Padding(12, 18, 12, 16)
        };

        var footer = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 45,
            Text = "  Native Windows shell\n  Explorer-compatible behavior",
            ForeColor = Color.FromArgb(151, 157, 166),
            Font = new Font("Segoe UI Variable Text", 8F),
            Padding = new Padding(4, 0, 0, 0)
        };
        sidebar.Controls.Add(footer);

        var links = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Color.Transparent
        };

        links.Controls.Add(CreateSectionLabel("EXPLORER"));
        links.Controls.Add(CreateNavButton("Home", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        links.Controls.Add(CreateNavButton("This PC", ThisPc));
        links.Controls.Add(CreateSectionLabel("KNOWN FOLDERS"));
        links.Controls.Add(CreateNavButton("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));
        links.Controls.Add(CreateNavButton("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
        links.Controls.Add(CreateNavButton("Downloads", GetDownloadsPath()));
        links.Controls.Add(CreateNavButton("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)));
        links.Controls.Add(CreateSectionLabel("DRIVES"));

        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
        {
            links.Controls.Add(CreateNavButton($"{drive.Name}  {drive.VolumeLabel}".Trim(), drive.RootDirectory.FullName));
        }

        sidebar.Controls.Add(links);
        return sidebar;
    }

    private Button CreateNavButton(string label, string target)
    {
        var button = new Button
        {
            Text = label,
            Width = 188,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(56, 59, 64), MouseDownBackColor = Color.FromArgb(66, 69, 75) },
            BackColor = Color.Transparent,
            ForeColor = Color.FromArgb(227, 230, 234),
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI Variable Text", 9F),
            Margin = new Padding(0, 1, 0, 1),
            Padding = new Padding(12, 0, 0, 0),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false
        };
        button.Click += (_, _) => Navigate(target);
        return button;
    }

    private static Label CreateSectionLabel(string text) => new()
    {
        AutoSize = false,
        Text = text,
        Width = 188,
        Height = 29,
        ForeColor = Color.FromArgb(145, 151, 160),
        Font = new Font("Segoe UI Variable Text", 7.5F, FontStyle.Bold),
        TextAlign = ContentAlignment.BottomLeft,
        Padding = new Padding(12, 0, 0, 3),
        Margin = new Padding(0, 9, 0, 1)
    };

    private static Button CreateToolButton(string text, string accessibleName, EventHandler onClick, int width = 34)
    {
        var button = new Button
        {
            Text = text,
            AccessibleName = accessibleName,
            Width = width,
            Height = 32,
            FlatStyle = FlatStyle.Flat,
            FlatAppearance = { BorderSize = 0, MouseOverBackColor = Color.FromArgb(59, 62, 67), MouseDownBackColor = Color.FromArgb(74, 78, 83) },
            BackColor = Color.FromArgb(47, 50, 54),
            ForeColor = Color.FromArgb(239, 240, 242),
            Font = new Font("Segoe UI Variable Text", 9F, FontStyle.Bold),
            Cursor = Cursors.Hand,
            Margin = new Padding(2, 0, 0, 0),
            UseVisualStyleBackColor = false
        };
        button.Click += onClick;
        return button;
    }

    private void AddressBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            Navigate(_addressBox.Text);
            e.SuppressKeyPress = true;
        }
    }

    private void Navigate(string target)
    {
        var normalized = target.Trim();
        if (normalized.Equals("This PC", StringComparison.OrdinalIgnoreCase))
        {
            normalized = ThisPc;
        }
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        SetStatus("Opening location…");
        if (normalized.Equals(ThisPc, StringComparison.OrdinalIgnoreCase))
        {
            ShowThisPc();
        }
        else
        {
            ShowShellView();
            _browser.Navigate(normalized);
        }
    }

    private static string FriendlyAddress(string target) => target switch
    {
        ThisPc => "This PC",
        _ => target
    };

    private void SetStatus(string message) => _statusLabel.Text = message;

    private Panel BuildThisPcOverview()
    {
        var overview = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Visible = false,
            Padding = new Padding(34, 30, 34, 30)
        };

        var title = new Label
        {
            AutoSize = true,
            Text = "This PC",
            ForeColor = Color.FromArgb(32, 34, 38),
            Font = new Font("Segoe UI Variable Display", 22F, FontStyle.Bold),
            Location = new Point(34, 30)
        };
        overview.Controls.Add(title);

        var subtitle = new Label
        {
            AutoSize = true,
            Text = "Drives and devices",
            ForeColor = Color.FromArgb(100, 106, 115),
            Font = new Font("Segoe UI Variable Text", 10F),
            Location = new Point(36, 68)
        };
        overview.Controls.Add(subtitle);

        var drives = new FlowLayoutPanel
        {
            Location = new Point(34, 108),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            Size = new Size(overview.Width - 68, overview.Height - 140),
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent
        };
        drives.Resize += (_, _) =>
        {
            foreach (Control card in drives.Controls)
            {
                card.Width = Math.Max(460, drives.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 4);
            }
        };

        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.IsReady))
        {
            drives.Controls.Add(CreateDriveCard(drive));
        }
        overview.Controls.Add(drives);
        return overview;
    }

    private Control CreateDriveCard(DriveInfo drive)
    {
        var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local drive" : drive.VolumeLabel;
        var usedRatio = drive.TotalSize == 0 ? 0 : (double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize;
        var card = new Panel
        {
            Width = 800,
            Height = 86,
            BackColor = Color.FromArgb(248, 249, 250),
            Margin = new Padding(0, 0, 0, 10),
            Cursor = Cursors.Hand,
            Padding = new Padding(18, 13, 18, 13)
        };
        card.Paint += (_, e) =>
        {
            using var border = new Pen(Color.FromArgb(224, 227, 230));
            e.Graphics.DrawRectangle(border, 0, 0, card.Width - 1, card.Height - 1);
        };

        var name = new Label
        {
            Text = $"{label} ({drive.Name.TrimEnd('\\')})",
            Font = new Font("Segoe UI Variable Text", 10F, FontStyle.Bold),
            ForeColor = Color.FromArgb(39, 42, 47),
            AutoSize = true,
            Location = new Point(18, 13)
        };
        var detail = new Label
        {
            Text = $"{FormatBytes(drive.AvailableFreeSpace)} free of {FormatBytes(drive.TotalSize)}",
            Font = new Font("Segoe UI Variable Text", 8.5F),
            ForeColor = Color.FromArgb(102, 108, 116),
            AutoSize = true,
            Location = new Point(18, 37)
        };
        var usage = new ProgressBar
        {
            Location = new Point(18, 60),
            Width = 280,
            Height = 7,
            Style = ProgressBarStyle.Continuous,
            Value = Math.Clamp((int)Math.Round(usedRatio * 100), 0, 100),
            TabStop = false
        };
        card.Controls.Add(name);
        card.Controls.Add(detail);
        card.Controls.Add(usage);

        void OpenDrive(object? sender, EventArgs eventArgs) => Navigate(drive.RootDirectory.FullName);
        card.Click += OpenDrive;
        name.Click += OpenDrive;
        detail.Click += OpenDrive;
        usage.Click += OpenDrive;
        return card;
    }

    private void ShowThisPc()
    {
        _browser.Visible = false;
        _thisPcOverview.Visible = true;
        _thisPcOverview.BringToFront();
        _addressBox.Text = ThisPc;
        SetStatus("Drives and devices");
    }

    private void ShowShellView()
    {
        _thisPcOverview.Visible = false;
        _browser.Visible = true;
        _browser.BringToFront();
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var index = 0;
        while (size >= 1024 && index < units.Length - 1)
        {
            size /= 1024;
            index++;
        }
        return $"{size:0.#} {units[index]}";
    }

    private static string GetDownloadsPath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidate = Path.Combine(profile, "Downloads");
        return Directory.Exists(candidate) ? candidate : profile;
    }
}
