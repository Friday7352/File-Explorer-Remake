using Clearspace.Native;

namespace Clearspace.Models;

public enum SortColumn
{
    Name,
    DateModified,
    Type,
    Size
}

/// <summary>
/// Explorer-compatible ordering: folders first, then natural ("logical") name order
/// so file10 sorts after file9 rather than after file1.
/// </summary>
public sealed class ItemComparer : IComparer<FileSystemItem>
{
    private readonly SortColumn _column;
    private readonly bool _descending;
    private readonly bool _foldersFirst;

    public ItemComparer(SortColumn column, bool descending, bool foldersFirst = true)
    {
        _column = column;
        _descending = descending;
        _foldersFirst = foldersFirst;
    }

    public int Compare(FileSystemItem? x, FileSystemItem? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        if (_foldersFirst && x.IsFolder != y.IsFolder)
            return x.IsFolder ? -1 : 1;

        var result = _column switch
        {
            SortColumn.DateModified => x.DateModified.CompareTo(y.DateModified),
            SortColumn.Size => x.Size.CompareTo(y.Size),
            SortColumn.Type => string.Compare(x.Extension, y.Extension, StringComparison.OrdinalIgnoreCase),
            _ => 0
        };

        // Fall back to name so equal keys still produce a stable, readable order.
        if (result == 0)
            result = NativeMethods.StrCmpLogicalW(x.Name, y.Name);

        return _descending ? -result : result;
    }
}
