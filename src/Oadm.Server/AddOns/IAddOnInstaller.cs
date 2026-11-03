namespace Oadm.Server.AddOns;

/// <summary>
/// Installs and removes the add-ons of this OADM version (docs/spec/add-ons.md "Server"). One install or remove at a time;
/// both are refused while a self update runs. Behind PluginService ListAddOns, InstallAddOn, RemoveAddOn and
/// GetAddOnClientPart.
/// </summary>
public interface IAddOnInstaller
{
    /// <summary>
    /// Every add-on of <see cref="AddOnCatalog"/> with its state on this server. <paramref name="checkReach"/>: whether the
    /// server reaches the release now (one check at a time, kept for a few minutes; the Plugins page's card); without it
    /// the last answer is used (a client's sync of its client parts never waits for GitHub).
    /// </summary>
    Task<IReadOnlyList<AddOnStatus>> ListAsync(bool checkReach, CancellationToken ct);

    /// <summary>An add-on that was removed and is still loaded (off until the restart): it cannot be turned on.</summary>
    bool IsRemoved(string packageId);

    /// <summary>
    /// Downloads the add-on of the server's version, checks it, swaps it in and loads it (off). Reports each step on
    /// <paramref name="progress"/>. <see cref="KeyNotFoundException"/> for an id not in the catalogue,
    /// <see cref="InvalidOperationException"/> when it is installed, another install or remove runs or a self update runs
    /// (<see cref="AddOnTexts.Updating"/>), <see cref="AddOnException"/> for a failed download or check (nothing left behind).
    /// </summary>
    /// <returns>The add-on's new state.</returns>
    Task<AddOnStatus> InstallAsync(string id, IProgress<AddOnProgress> progress, CancellationToken ct);

    /// <summary>
    /// Turns the add-on off at once and keeps it loaded; its folder goes at the next server start. The exceptions of
    /// <see cref="InstallAsync"/> (not installed: <see cref="InvalidOperationException"/>).
    /// </summary>
    /// <returns>The add-on's new state (<see cref="AddOnState.Removed"/>).</returns>
    Task<AddOnStatus> RemoveAsync(string id, CancellationToken ct);

    /// <summary>
    /// The client part of an installed add-on with each file's SHA-256, read and hashed once per version.
    /// <see cref="KeyNotFoundException"/> for an unknown or not installed add-on, <see cref="InvalidOperationException"/>
    /// (<see cref="AddOnTexts.ClientPartBusy"/>) while the same <paramref name="caller"/> (a session) waits for one already.
    /// </summary>
    Task<AddOnClientPart> GetClientPartAsync(string id, string caller, CancellationToken ct);
}

/// <summary>
/// The release files of add-ons: downloads one file of OADM's own release (only from <c>GitHubReleaseFeed.AllowedHosts</c>,
/// HTTPS, with the limits of self update) and checks it against the release's <c>SHA256SUMS.txt</c>. Faked in tests: never a
/// real download there.
/// </summary>
public interface IAddOnFiles
{
    /// <summary>Whether the server reaches the release of <paramref name="version"/> now (the Plugins page's Install).</summary>
    Task<bool> CanReachAsync(string version, CancellationToken ct);

    /// <summary>
    /// Downloads <paramref name="fileName"/> of the release of <paramref name="version"/> to <paramref name="targetPath"/>
    /// and checks its SHA-256 against the release's <c>SHA256SUMS.txt</c>. Reports the download in percent (0 to 100).
    /// <see cref="AddOnException"/> with <see cref="AddOnTexts.DownloadFailed"/> or <see cref="AddOnTexts.Damaged"/>; the
    /// target file does not exist after a failure.
    /// </summary>
    Task DownloadAsync(string version, string fileName, string targetPath, IProgress<int>? progress, CancellationToken ct);
}
