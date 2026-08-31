using System.IO;
using Microsoft.Win32;
using Clearspace.Models;
using Clearspace.Native;

namespace Clearspace.Services;

/// <summary>One local folder that a cloud provider keeps in sync.</summary>
/// <param name="Name">What to call it in the sidebar, e.g. "OneDrive - Personal".</param>
/// <param name="Path">The local root on disk.</param>
/// <param name="Provider">The sync engine, e.g. "OneDrive" or "Dropbox".</param>
public sealed record CloudRoot(string Name, string Path, string Provider);

/// <summary>
/// Finds the cloud-synced folders on this machine and reads per-item sync status.
///
/// Two separate jobs, kept together because they are two halves of one feature:
///
/// Discovery reads the registry rather than guessing at %UserProfile%\OneDrive.
/// A OneDrive folder is routinely relocated, and a machine can hold a personal
/// account and one or more work tenants at once, each with its own root. The
/// SyncRootManager key is the shell's own list, so Dropbox, Google Drive, and
/// iCloud are picked up for free by the same pass.
///
/// Status needs no API call at all. Files On-Demand records its state in file
/// attributes, and those attributes are already in the WIN32_FIND_DATA that the
/// directory enumerator reads, so a whole folder's sync status is free.
/// </summary>
public static class CloudStorageService
{
    private static IReadOnlyList<CloudRoot>? _roots;
    private static readonly object Gate = new();

    /// <summary>Every cloud root found on this machine, in a stable order.</summary>
    public static IReadOnlyList<CloudRoot> Roots
    {
        get
        {
            if (_roots is not null)
                return _roots;

            lock (Gate)
                return _roots ??= Discover();
        }
    }

    /// <summary>Re-reads the registry. Used after a provider is added or signed out of.</summary>
    public static void Invalidate()
    {
        lock (Gate)
            _roots = null;
    }

    /// <summary>
    /// True once discovery has actually run. Callers that must not block on it —
    /// the sidebar build that happens before the first frame — check this and skip
    /// rather than paying for registry reads on the UI thread.
    /// </summary>
    public static bool IsDiscovered
    {
        get
        {
            lock (Gate)
                return _roots is not null;
        }
    }

    /// <summary>The root a path belongs to, or null when it is outside every sync folder.</summary>
    public static CloudRoot? RootFor(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        // Longest match wins: a work tenant's folder can sit inside the personal one.
        return Roots
            .Where(root => IsWithin(path, root.Path))
            .OrderByDescending(root => root.Path.Length)
            .FirstOrDefault();
    }

    public static bool IsCloudPath(string? path) => RootFor(path) is not null;

    private static bool IsWithin(string path, string root)
    {
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return false;

        // Guard against "C:\OneDrive Backup" matching the root "C:\OneDrive".
        return path.Length == root.Length ||
               path[root.Length] is '\\' or '/' ||
               root.EndsWith('\\');
    }

    // ---------- Per-item status ----------

    /// <summary>
    /// Reads sync state out of raw file attributes.
    ///
    /// The order matters. A dehydrated item carries both the recall bit and the
    /// unpinned bit, so recall has to be tested first or every online-only file
    /// would report as merely available.
    /// </summary>
    public static CloudSyncState Evaluate(uint attributes)
    {
        const uint managed =
            NativeMethods.FILE_ATTRIBUTE_PINNED |
            NativeMethods.FILE_ATTRIBUTE_UNPINNED |
            NativeMethods.FILE_ATTRIBUTE_RECALL_ON_OPEN |
            NativeMethods.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS;

        if ((attributes & managed) == 0)
            return CloudSyncState.None;

        if ((attributes & (NativeMethods.FILE_ATTRIBUTE_RECALL_ON_OPEN |
                           NativeMethods.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS)) != 0)
            return CloudSyncState.OnlineOnly;

        return (attributes & NativeMethods.FILE_ATTRIBUTE_PINNED) != 0
            ? CloudSyncState.AlwaysAvailable
            : CloudSyncState.Available;
    }

    // ---------- Pinning ----------

    /// <summary>
    /// Asks the provider to keep a local copy, or to release one.
    ///
    /// There is a Cloud Filter API for this, but writing the attribute is what the
    /// shell's own menu items do and it works with every provider that implements
    /// Files On-Demand, with no dependency on cldapi.dll being present. The two
    /// bits are mutually exclusive: setting one has to clear the other, or the
    /// sync engine sees a contradiction and ignores both.
    ///
    /// Folders are walked, because the attribute on a directory governs new items
    /// created in it rather than the ones already there.
    /// </summary>
    public static void SetPinned(string path, bool pinned, CancellationToken cancellationToken = default)
    {
        Apply(path, pinned);

        if (!Directory.Exists(path))
            return;

        foreach (var item in DirectoryEnumerator.EnumerateTree(path, showHidden: true, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Apply(item.FullPath, pinned);
        }
    }

    private static void Apply(string path, bool pinned)
    {
        var current = NativeMethods.GetFileAttributesW(path);

        if (current == NativeMethods.INVALID_FILE_ATTRIBUTES)
            return;

        var updated = pinned
            ? (current | NativeMethods.FILE_ATTRIBUTE_PINNED) & ~NativeMethods.FILE_ATTRIBUTE_UNPINNED
            : (current | NativeMethods.FILE_ATTRIBUTE_UNPINNED) & ~NativeMethods.FILE_ATTRIBUTE_PINNED;

        if (updated != current)
            NativeMethods.SetFileAttributesW(path, updated);
    }

    // ---------- Discovery ----------

    private static IReadOnlyList<CloudRoot> Discover()
    {
        var found = new List<CloudRoot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? name, string? path, string provider)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(name))
                return;

            path = path.TrimEnd('\\');

            if (!Directory.Exists(path) || !seen.Add(path))
                return;

            found.Add(new CloudRoot(name, path, provider));
        }

        foreach (var (name, path) in ReadOneDriveAccounts())
            Add(name, path, "OneDrive");

        foreach (var (provider, name, path) in ReadSyncRootManager())
            Add(name, path, provider);

        // Last resort. OneDrive publishes these for scripts, and they survive even
        // when the account keys are laid out differently than expected.
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDriveConsumer"), "OneDrive");
        Add("OneDrive for Business", Environment.GetEnvironmentVariable("OneDriveCommercial"), "OneDrive");
        Add("OneDrive", Environment.GetEnvironmentVariable("OneDrive"), "OneDrive");

        return found;
    }

    /// <summary>
    /// OneDrive's own account keys. These carry the friendliest names: the tenant
    /// name for a work account and the account email for a personal one.
    /// </summary>
    private static List<(string Name, string Path)> ReadOneDriveAccounts()
    {
        var accounts = new List<(string, string)>();

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts");

            if (root is null)
                return accounts;

            foreach (var accountId in root.GetSubKeyNames())
            {
                using var account = root.OpenSubKey(accountId);

                if (account?.GetValue("UserFolder") is not string folder ||
                    string.IsNullOrWhiteSpace(folder))
                    continue;

                var isBusiness = accountId.StartsWith("Business", StringComparison.OrdinalIgnoreCase);
                var label = account.GetValue("DisplayName") as string;

                var name = !string.IsNullOrWhiteSpace(label)
                    ? $"OneDrive - {label}"
                    : isBusiness ? "OneDrive for Business" : "OneDrive";

                accounts.Add((name, folder));
            }
        }
        catch (Exception)
        {
            // A missing or unreadable key simply means no accounts from this source.
        }

        return accounts;
    }

    /// <summary>
    /// The shell's register of cloud providers. Subkey ids look like
    /// "OneDrive!S-1-5-21-...!Personal!{guid}", and the actual local path sits in
    /// a UserSyncRoots subkey keyed by the user's SID.
    /// </summary>
    private static List<(string Provider, string Name, string Path)> ReadSyncRootManager()
    {
        var roots = new List<(string, string, string)>();

        try
        {
            using var manager = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager");

            if (manager is null)
                return roots;

            foreach (var id in manager.GetSubKeyNames())
            {
                using var entry = manager.OpenSubKey(id);
                using var userRoots = entry?.OpenSubKey("UserSyncRoots");

                if (userRoots is null)
                    continue;

                var provider = id.Split('!')[0];

                foreach (var valueName in userRoots.GetValueNames())
                {
                    if (userRoots.GetValue(valueName) is not string path ||
                        string.IsNullOrWhiteSpace(path))
                        continue;

                    // The folder's own name is the best label available here; the
                    // DisplayNameResource on the key is an indirect string that
                    // needs a resource load to become readable.
                    var name = System.IO.Path.GetFileName(path.TrimEnd('\\'));

                    if (string.IsNullOrWhiteSpace(name))
                        name = provider;

                    roots.Add((provider, name, path));
                }
            }
        }
        catch (Exception)
        {
            // Same as above: absence just means nothing to add.
        }

        return roots;
    }
}
