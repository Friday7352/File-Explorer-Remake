namespace Clearspace.Shell;

/// <summary>
/// Hosts the Windows Explorer shell view. The shell, not Clearspace, renders items
/// and supplies the familiar context menus, file associations, thumbnails, and commands.
/// </summary>
internal sealed class ShellBrowserHost : Panel
{
    private IExplorerBrowser? _browser;
    private bool _initialized;
    private readonly List<IntPtr> _navigationPidls = [];

    public event EventHandler<string>? NavigationRequested;
    public event EventHandler<string>? NavigationFailed;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ResizeBrowser();
    }

    public bool Navigate(string parsingName)
    {
        if (!_initialized || _browser is null)
        {
            return false;
        }

        IntPtr pidl = IntPtr.Zero;
        try
        {
            var parseResult = ShellNative.SHParseDisplayName(parsingName, IntPtr.Zero, out pidl, 0, out _);
            if (parseResult < 0 || pidl == IntPtr.Zero)
            {
                NavigationFailed?.Invoke(this, parsingName);
                return false;
            }

            var result = _browser.BrowseToIDList(pidl, 0);
            if (result < 0)
            {
                NavigationFailed?.Invoke(this, parsingName);
                return false;
            }

            KeepPidl(pidl);
            pidl = IntPtr.Zero;
            QueueExplorerThemeRefresh();
            NavigationRequested?.Invoke(this, parsingName);
            return true;
        }
        catch (Exception exception)
        {
            NavigationFailed?.Invoke(this, exception.Message);
            return false;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                ShellNative.CoTaskMemFree(pidl);
            }
        }
    }

    public bool NavigateKnownFolder(Guid knownFolderId, string displayName)
    {
        if (!_initialized || _browser is null)
        {
            return false;
        }

        IntPtr pidl = IntPtr.Zero;
        try
        {
            var knownFolderResult = ShellNative.SHGetKnownFolderIDList(ref knownFolderId, 0, IntPtr.Zero, out pidl);
            if (knownFolderResult < 0 || pidl == IntPtr.Zero)
            {
                NavigationFailed?.Invoke(this, displayName);
                return false;
            }

            var result = _browser.BrowseToIDList(pidl, 0);
            if (result < 0)
            {
                NavigationFailed?.Invoke(this, displayName);
                return false;
            }

            KeepPidl(pidl);
            pidl = IntPtr.Zero;
            QueueExplorerThemeRefresh();
            NavigationRequested?.Invoke(this, displayName);
            return true;
        }
        catch (Exception exception)
        {
            NavigationFailed?.Invoke(this, exception.Message);
            return false;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                ShellNative.CoTaskMemFree(pidl);
            }
        }
    }

    public void GoBack() => BrowseHistory(ShellNative.NavigateBack);
    public void GoForward() => BrowseHistory(ShellNative.NavigateForward);
    public void GoUp() => BrowseHistory(ShellNative.NavigateUp);

    /// <summary>Starts the hosted shell only after the containing form is visible.</summary>
    public void Start()
    {
        InitializeBrowser();
    }

    private void InitializeBrowser()
    {
        if (_initialized || !IsHandleCreated)
        {
            return;
        }

        try
        {
            // The hosted shell view uses its own controls. Opt its parent into the
            // Explorer dark theme before those controls are created so names remain readable.
            ShellNative.SetWindowTheme(Handle, "DarkMode_Explorer", null);
            _browser = (IExplorerBrowser)new ExplorerBrowserClass();
            var rect = new NativeRect(ClientSize.Width, ClientSize.Height);
            var result = _browser.Initialize(Handle, ref rect, IntPtr.Zero);
            if (result < 0)
            {
                throw new InvalidOperationException($"Windows shell view could not start (0x{result:X8}).");
            }

            // Initialize requires explicit view settings when no settings were passed to it.
            // Details is Explorer's standard dense daily-use view.
            var settings = new FolderSettings(viewMode: 4, flags: 0);
            _browser.SetFolderSettings(ref settings);
            _browser.SetEmptyText("This folder is empty.");
            _initialized = true;
            ApplyExplorerThemeToHostedControls();
        }
        catch (Exception exception)
        {
            _browser = null;
            NavigationFailed?.Invoke(this, exception.Message);
        }
    }

    private void ResizeBrowser()
    {
        if (!_initialized || _browser is null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        _browser.SetRect(IntPtr.Zero, new NativeRect(ClientSize.Width, ClientSize.Height));
    }

    private void BrowseHistory(uint flag)
    {
        if (_initialized && _browser is not null)
        {
            _browser.BrowseToIDList(IntPtr.Zero, flag);
        }
    }

    private void QueueExplorerThemeRefresh()
    {
        if (IsHandleCreated)
        {
            BeginInvoke((MethodInvoker)ApplyExplorerThemeToHostedControls);
            var retry = new System.Windows.Forms.Timer { Interval = 180 };
            var passes = 0;
            retry.Tick += (_, _) =>
            {
                ApplyExplorerThemeToHostedControls();
                if (++passes >= 3)
                {
                    retry.Stop();
                    retry.Dispose();
                }
            };
            retry.Start();
        }
    }

    private void ApplyExplorerThemeToHostedControls()
    {
        ShellNative.SetWindowTheme(Handle, "DarkMode_Explorer", null);
        ShellNative.EnumChildWindows(Handle, (childHandle, _) =>
        {
            ShellNative.SetWindowTheme(childHandle, "DarkMode_Explorer", null);
            return true;
        }, IntPtr.Zero);
    }

    // ExplorerBrowser can complete navigation asynchronously. Keep successful PIDLs alive
    // for the lifetime of this hosted view rather than releasing a virtual-folder PIDL early.
    private void KeepPidl(IntPtr pidl) => _navigationPidls.Add(pidl);

    protected override void Dispose(bool disposing)
    {
        if (disposing && _browser is not null)
        {
            try
            {
                _browser.Destroy();
            }
            catch
            {
                // The shell process may already have released the hosted view.
            }
            finally
            {
                _browser = null;
            }
        }

        if (disposing)
        {
            foreach (var pidl in _navigationPidls)
            {
                ShellNative.CoTaskMemFree(pidl);
            }
            _navigationPidls.Clear();
        }

        base.Dispose(disposing);
    }
}
