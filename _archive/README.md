# Archive

Superseded work, kept for reference. Nothing here builds or is referenced by the
current app, and all of it is in git history regardless, so it can be deleted
outright whenever it stops being useful.

## WPF prototype

`App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`,
`NetworkDriveDialog.cs`, `Clearspace.Desktop.csproj`

The original visual prototype. Its file list was a custom approximation of
Explorer rather than real shell behavior. Useful as a record of the visual
direction.

## WinForms prototype

`MainForm.cs`, `Program.cs`, `FileExplorerRemake.csproj`

The earliest version. Superseded entirely.

## ShellParity

The `IExplorerBrowser` hosting experiment. It worked, and the interop in
`ShellBrowserHost.cs` is correct and worth rereading if HWND hosting ever comes
back up. It was abandoned because hosting the shell view means the file list
cannot be themed, measured, or extended, which is the whole point of Clearspace.

Two things it demonstrated that are worth remembering:

- Theming a hosted shell view requires chasing its child windows with
  `EnumChildWindows` on a retry timer, and never fully lands.
- Without subscribing to `IExplorerBrowserEvents` via `Advise`, the host cannot
  tell when the user navigates inside the view, so the address bar goes stale.

## Dead launchers

`Run Latest Clearspace.cmd` pointed at `publish-latest-fixed`.
`Run Clearspace Shell Preview.cmd` pointed at `release\clearspace-modern`.
Both targets are gone.
