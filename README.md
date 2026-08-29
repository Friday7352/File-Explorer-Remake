# Clearspace

A Windows file manager that owns its own file list, so it can be measured, tuned,
and extended.

## Running it

Run `Build Clearspace.cmd` once. It publishes a single executable to `dist\` and
puts a shortcut on your desktop. After that, just use the shortcut.

Rerun it after changing code. Requires the .NET 10 SDK; the published app needs
the .NET 10 Desktop Runtime.

`Run Clearspace.cmd` is the development launcher. It rebuilds first, which is
what you want while working, and is slower as a result.

`Clean.cmd` removes all build output.

## Layout

```
Clearspace/     The application. See Clearspace/ARCHITECTURE.md.
dist/           Published executable, produced by Build Clearspace.cmd.
_archive/       Superseded prototypes, kept for reference. See _archive/README.md.
```

## Approach

Clearspace renders and enumerates files itself rather than hosting Explorer's
shell view. Hosting is far less work up front and was tried first, but a hosted
view cannot be themed, measured, or extended, which rules out both of the goals
above. `_archive/README.md` records what that experiment showed.

Destructive operations still go through the shell, so deletes land in the Recycle
Bin and copies get Windows' own progress and conflict dialogs.

Architecture, performance notes, and how to add a feature are in
`Clearspace/ARCHITECTURE.md`.
