# Clearspace parity baseline

Clearspace will not be presented as an Explorer replacement until these are working against the Windows shell rather than custom approximations.

## Shell behavior

- Shell namespace navigation: Home, This PC, known folders, drives, network locations, and special folders
- Native file associations, thumbnail/property data, and Windows shell context menus
- Clipboard and drag/drop behavior compatible with Explorer
- Recycle Bin deletion and the Windows file-operation progress/conflict experience

## Explorer interaction

- Back/forward/up history, address bar, breadcrumb navigation, selection, keyboard shortcuts, and list modes
- Sort, group, filter, rename, copy, move, delete, and create folders
- Standard context-menu submenus and Properties

## Clearspace layer

- A neutral graphite visual shell around the above behavior
- Search/indexing and other enhancements only after the baseline is stable

The current WPF app is a visual prototype only. New work will live in a dedicated shell-parity layer rather than extending its custom file-list behavior.
