namespace Oadm.Server.AddOns;

/// <summary>
/// Installs and removes the add-ons of this OADM version (docs/spec/add-ons.md "Server"). One install or remove at a time;
/// both are refused while a self update runs. Behind PluginService ListAddOns, InstallAddOn, UploadAddOn, RemoveAddOn and
/// GetAddOnClientPart.
/// </summary>
public interface IAddOnInstaller
{
    /// <summary>Every add-on of <see cref="AddOnCatalog"/> with its state on this server.</summary>
    Task<IReadOnlyList<AddOnStatus>> ListAsync(CancellationToken ct);

    /// <summary>An add-on that was removed and is still loaded (off until the restart): it cannot be turned on.</summary>
    bool IsRemoved(string packageId);

    /// <summary>
    /// Takes back an add-on that was removed and is still loaded; nothing to send. <see cref="KeyNotFoundException"/> for an id
    /// not in the catalogue, <see cref="InvalidOperationException"/> in any other state, while another install or remove runs
    /// or while a self update runs (<see cref="AddOnTexts.Updating"/>).
    /// </summary>
    /// <returns>The add-on's new state.</returns>
    Task<AddOnStatus> InstallAsync(string id, IProgress<AddOnProgress> progress, CancellationToken ct);

    /// <summary>
    /// Installs an add-on from a file the user chose: receives it into a temporary file, checks it against the hash built
    /// into this server, unpacks it into a new folder, checks that folder, swaps it in and loads it (off). The exceptions of
    /// <see cref="InstallAsync"/>, also for a build without the hash (<see cref="AddOnTexts.NoHash"/>);
    /// <see cref="AddOnException"/> for a refused file. Any failure leaves the state as it was.
    /// </summary>
    /// <returns>The add-on's new state.</returns>
    Task<AddOnStatus> InstallFileAsync(AddOnUpload upload, IProgress<AddOnProgress> progress, CancellationToken ct);

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
