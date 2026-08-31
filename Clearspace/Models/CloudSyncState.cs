namespace Clearspace.Models;

/// <summary>
/// Where a cloud-synced item's contents actually live right now.
///
/// This mirrors the three states OneDrive's Files On-Demand exposes in Explorer's
/// Status column. Anything outside a sync root is <see cref="None"/> and shows
/// nothing at all, so the column stays quiet in ordinary folders.
/// </summary>
public enum CloudSyncState
{
    /// <summary>Not managed by a cloud provider.</summary>
    None,

    /// <summary>Contents live in the cloud. Opening it downloads it first.</summary>
    OnlineOnly,

    /// <summary>Downloaded and usable offline, but the provider may reclaim the space.</summary>
    Available,

    /// <summary>Pinned. The provider must keep a local copy.</summary>
    AlwaysAvailable
}
