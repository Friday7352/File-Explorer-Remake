using System.IO;
using System.Runtime.InteropServices;

namespace Clearspace.Services;

/// <summary>
/// One file or folder, as the index stores it.
///
/// A struct in a flat array rather than an object on the heap: a million of these
/// is forty megabytes in one allocation with perfect locality, where a million
/// objects would be a million allocations the GC has to walk on every collection.
/// The name is not here - it lives in the volume's shared character pool, which is
/// what keeps this fixed size and scannable.
/// </summary>
// Sequential layout, because these bytes are written to disk as-is. Auto layout
// lets the runtime reorder fields, which would quietly change the on-disk format
// between builds.
[StructLayout(LayoutKind.Sequential)]
internal struct IndexEntry
{
    public long Size;
    public long ModifiedTicks;
    public long CreatedTicks;

    /// <summary>Start of this entry's name in the volume's name pool.</summary>
    public int NameOffset;

    /// <summary>
    /// The entry this one lives in, or -1 for a volume root. Full paths are built
    /// by walking this chain, which is why no path is stored: doing so would
    /// roughly triple the pool to hold prefixes that are already known.
    /// </summary>
    public int ParentIndex;

    public FileAttributes Attributes;
    public ushort NameLength;

    public readonly bool IsFolder => (Attributes & FileAttributes.Directory) != 0;

    public readonly bool IsHiddenOrSystem =>
        (Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
}

/// <summary>
/// Every file and folder on one volume, held in memory.
///
/// Built by a single thread and then treated as read-only, which is what lets the
/// search path run without a lock: a rebuild produces a new instance and the
/// service swaps it in, rather than mutating one that queries are reading.
/// </summary>
internal sealed class VolumeIndex
{
    private const int InitialEntries = 4096;
    private const int InitialPool = 65536;

    private IndexEntry[] _entries;
    private char[] _names;

    /// <summary>
    /// The same names, lower-cased, at identical offsets.
    ///
    /// This is not redundancy, it is the whole reason a query stays fast.
    /// Case-insensitive comparison in .NET is several times slower than ordinal,
    /// and a search touches every name in the volume. Folding once at build time
    /// turns every keystroke into an ordinal scan over plain characters.
    /// </summary>
    private char[] _folded;

    private int _count;
    private int _poolLength;

    public VolumeIndex(string root, uint serialNumber)
    {
        Root = root;
        SerialNumber = serialNumber;
        BuiltUtc = DateTime.UtcNow;
        _entries = new IndexEntry[InitialEntries];
        _names = new char[InitialPool];
        _folded = new char[InitialPool];
    }

    /// <summary>Adopts already-built buffers. Used when loading from disk.</summary>
    internal VolumeIndex(
        string root,
        uint serialNumber,
        DateTime builtUtc,
        IndexEntry[] entries,
        int count,
        char[] names,
        int poolLength)
    {
        Root = root;
        SerialNumber = serialNumber;
        BuiltUtc = builtUtc;
        _entries = entries;
        _count = count;
        _names = names;
        _poolLength = poolLength;

        // Regenerated rather than persisted: a lower-casing pass over the pool is
        // faster than reading the same number of bytes back off disk, and halves
        // what the file costs on disk.
        //
        // Parallel because on a large machine this is half a gigabyte of
        // characters, and done serially it is the slowest part of loading - which
        // is precisely the moment the user is waiting to be able to search.
        _folded = new char[names.Length];

        const int FoldChunk = 1 << 16;
        var source = names;
        var target = _folded;
        var chunks = (poolLength + FoldChunk - 1) / FoldChunk;

        if (chunks > 0)
        {
            Parallel.For(0, chunks, chunk =>
            {
                var start = chunk * FoldChunk;
                var end = Math.Min(poolLength, start + FoldChunk);

                for (var i = start; i < end; i++)
                    target[i] = char.ToLowerInvariant(source[i]);
            });
        }
    }

    /// <summary>The volume this describes, e.g. "C:\" or "\\server\share\".</summary>
    public string Root { get; }

    /// <summary>Guards against a reformatted drive or a recycled drive letter.</summary>
    public uint SerialNumber { get; }

    public DateTime BuiltUtc { get; }

    public int Count => _count;

    internal IndexEntry[] Entries => _entries;

    internal char[] Names => _names;

    internal int PoolLength => _poolLength;

    /// <summary>
    /// Roughly what this volume costs in memory: the entry array plus both
    /// character pools.
    ///
    /// Worth being able to state plainly. Holding every name on a volume in RAM is
    /// the entire trick, and on a machine with millions of files that is not a
    /// rounding error - it is the single largest thing the app allocates, and the
    /// user should be able to see the number rather than discover it in Task
    /// Manager.
    /// </summary>
    public long EstimatedBytes =>
        ((long)_count * Marshal.SizeOf<IndexEntry>()) + ((long)_poolLength * sizeof(char) * 2);

    /// <summary>Appends an entry and returns its index, which children use as their parent.</summary>
    public int Add(
        int parentIndex,
        ReadOnlySpan<char> name,
        long size,
        long modifiedTicks,
        long createdTicks,
        FileAttributes attributes)
    {
        if (_count == _entries.Length)
            Array.Resize(ref _entries, _entries.Length * 2);

        while (_poolLength + name.Length > _names.Length)
        {
            var grown = _names.Length * 2;
            Array.Resize(ref _names, grown);
            Array.Resize(ref _folded, grown);
        }

        name.CopyTo(_names.AsSpan(_poolLength));

        for (var i = 0; i < name.Length; i++)
            _folded[_poolLength + i] = char.ToLowerInvariant(name[i]);

        _entries[_count] = new IndexEntry
        {
            Size = size,
            ModifiedTicks = modifiedTicks,
            CreatedTicks = createdTicks,
            NameOffset = _poolLength,
            ParentIndex = parentIndex,
            Attributes = attributes,
            NameLength = (ushort)name.Length
        };

        _poolLength += name.Length;
        return _count++;
    }

    /// <summary>Trims the buffers to what was actually used, once a build is finished.</summary>
    public void Compact()
    {
        if (_entries.Length != _count)
            Array.Resize(ref _entries, _count);

        if (_names.Length != _poolLength)
        {
            Array.Resize(ref _names, _poolLength);
            Array.Resize(ref _folded, _poolLength);
        }
    }

    public ReadOnlySpan<char> NameSpan(int index)
    {
        ref var entry = ref _entries[index];
        return _names.AsSpan(entry.NameOffset, entry.NameLength);
    }

    public string GetName(int index) => new(NameSpan(index));

    public ref readonly IndexEntry Entry(int index) => ref _entries[index];

    /// <summary>The deepest path this will reconstruct. Windows itself gives up well before here.</summary>
    private const int MaxChain = 256;

    /// <summary>
    /// Scratch space for building paths, one buffer per thread.
    ///
    /// This runs once per search result, and a search can return thousands, so the
    /// obvious version - a List for the chain, a StringBuilder for the text, then
    /// ToString - was three allocations per result for a string that is usually
    /// under a hundred characters. Reusing a buffer leaves one: the string itself.
    /// </summary>
    [ThreadStatic]
    private static char[]? _pathBuffer;

    /// <summary>
    /// Rebuilds a full path by walking the parent chain. Called only for results
    /// actually being shown, never across the index as a whole.
    /// </summary>
    public string GetPath(int index)
    {
        if (index < 0 || index >= _count)
            return string.Empty;

        // Depth, not breadth: a path is a handful of components even on a deeply
        // nested drive, so the chain fits on the stack.
        Span<int> chain = stackalloc int[MaxChain];
        var depth = 0;
        var current = index;

        while (current >= 0 && depth < MaxChain)
        {
            chain[depth++] = current;
            current = _entries[current].ParentIndex;
        }

        // One separator allowance per component is always enough.
        var capacity = 0;
        for (var i = 0; i < depth; i++)
            capacity += _entries[chain[i]].NameLength + 1;

        var buffer = _pathBuffer;

        if (buffer is null || buffer.Length < capacity)
            _pathBuffer = buffer = new char[Math.Max(512, capacity)];

        var written = 0;

        for (var i = depth - 1; i >= 0; i--)
        {
            var entry = _entries[chain[i]];

            // The root already carries its own separator ("C:\"), so only join
            // between components that do not.
            if (written > 0 && buffer[written - 1] != Path.DirectorySeparatorChar)
                buffer[written++] = Path.DirectorySeparatorChar;

            _names.AsSpan(entry.NameOffset, entry.NameLength).CopyTo(buffer.AsSpan(written));
            written += entry.NameLength;
        }

        return new string(buffer, 0, written);
    }

    /// <summary>
    /// Case-folds a search term so it can be compared against the folded pool.
    /// Callers must use this rather than ToLower of their own, or the two sides
    /// will disagree on characters where invariant folding differs.
    /// </summary>
    public static string Fold(string term) => term.ToLowerInvariant();

    /// <summary>
    /// Every entry whose name contains all of the folded terms.
    ///
    /// A straight scan, parallelised across ranges. There is no tree or trie here
    /// on purpose: at a million entries of about twenty characters the whole
    /// haystack is forty megabytes of contiguous chars, and scanning it is faster
    /// than any structure that would have to be built, kept current, and paged in.
    /// </summary>
    public List<int> Search(
        IReadOnlyList<string> foldedTerms,
        bool showHidden,
        bool foldersOnly,
        bool filesOnly,
        int limit,
        CancellationToken token)
    {
        var results = new List<int>();

        if (_count == 0 || foldedTerms.Count == 0 || limit <= 0)
            return results;

        const int ChunkSize = 4096;
        var chunks = (_count + ChunkSize - 1) / ChunkSize;
        var gate = new object();
        var total = 0;

        try
        {
            Parallel.For(0, chunks, new ParallelOptions { CancellationToken = token }, chunk =>
            {
                // The cap is checked per chunk rather than per entry: overshooting
                // by part of one chunk costs nothing and keeps the inner loop free
                // of a contended read.
                if (Volatile.Read(ref total) >= limit)
                    return;

                var start = chunk * ChunkSize;
                var end = Math.Min(_count, start + ChunkSize);
                List<int>? local = null;

                for (var i = start; i < end; i++)
                {
                    if (!showHidden && _entries[i].IsHiddenOrSystem)
                        continue;

                    var isFolder = _entries[i].IsFolder;

                    if (foldersOnly && !isFolder)
                        continue;

                    if (filesOnly && isFolder)
                        continue;

                    var name = _folded.AsSpan(_entries[i].NameOffset, _entries[i].NameLength);
                    var matched = true;

                    for (var t = 0; t < foldedTerms.Count; t++)
                    {
                        if (name.IndexOf(foldedTerms[t].AsSpan()) < 0)
                        {
                            matched = false;
                            break;
                        }
                    }

                    if (matched)
                        (local ??= []).Add(i);
                }

                if (local is null)
                    return;

                lock (gate)
                {
                    results.AddRange(local);
                    total = results.Count;
                }
            });
        }
        catch (OperationCanceledException)
        {
            return [];
        }

        if (results.Count > limit)
            results.RemoveRange(limit, results.Count - limit);

        return results;
    }
}
