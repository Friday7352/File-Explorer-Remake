# Clearspace

A file manager that owns its own list, so it can be measured, tuned, and extended.

Build and run:

```
dotnet build -c Debug
dotnet run -c Debug
```

Requires the .NET 10 SDK. Targets `net10.0-windows`, x64.

## Why WPF and not WinUI 3

Startup time is a stated goal. A packaged WinUI 3 app pays for package activation
and a heavier framework load before the first frame; WPF starts in a fraction of
that and needs no packaging to run. WPF also virtualizes long lists well, which is
the case that matters here.

## Where the speed comes from

Three decisions, all in the load path:

**One pass over the directory.** `DirectoryEnumerator` calls `FindFirstFileEx` with
`FindExInfoBasic` and `FIND_FIRST_EX_LARGE_FETCH`. The find data already carries the
size, attributes, and timestamps, so a finished row costs no extra disk access.
`Directory.EnumerateFileSystemEntries` hands back strings, which then need a stat
call each.

**Icons are cached by extension, not by file.** A folder with 80,000 photos makes one
shell call, not 80,000. `SHGFI_USEFILEATTRIBUTES` tells the shell to answer from the
registry rather than open the file. Only types that carry their own icon (`.exe`,
`.lnk`, `.ico`) bypass the cache.

**Loads are cancellable.** Navigating away cancels the in-flight listing, so a slow
network share never blocks the next folder.

The status bar prints the load time for each folder, so regressions are visible
while you work.

## Adding a feature

Everything the app can do is an `IAction`. To add one:

1. Write a class implementing `IAction` (see `Commands/Actions/`). Declare a label,
   a glyph, a hotkey, and when it is executable.
2. Add one line to `CommandManager.Register`.

The key binding, the enabled/disabled state, the tooltip, and the menu entry all
follow from what the action declared. `MainWindow.OnPreviewKeyDown` is the entire
keyboard layer and never needs editing.

Files uses a Roslyn generator to remove even step 2. Worth doing once the shape of
the command list settles.

## Layout

```
Native/       FindFirstFileEx enumeration and Win32 declarations
Models/       FileSystemItem, natural-order sorting
Services/     Icons, shell file operations, navigation history
Commands/     IAction, the registry, and the actions themselves
ViewModels/   MainViewModel: loading, sorting, status, sidebar
```

## Not built yet

- Tabs and split panes
- Real per-file thumbnails (images and video) via `IShellItemImageFactory`
- Native shell context menus via `IContextMenu` (the current menu is Clearspace's own)
- Drag and drop
- Search
- Settings persistence
- Watching the folder for changes with `ReadDirectoryChangesW`

Credit: the command architecture is modelled on the Files project
(https://github.com/files-community/Files, MIT). The code here is original.
