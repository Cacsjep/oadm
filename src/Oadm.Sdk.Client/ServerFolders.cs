namespace Oadm.Sdk.Client;

/// <summary>One folder on the server's computer: its name ("Plates", a drive "Data (D:)") and its full path.</summary>
/// <param name="Name">The name to show.</param>
/// <param name="Path">The full path on the server.</param>
/// <param name="IsRoot">A drive or top folder of the list of roots.</param>
public sealed record ServerFolder(string Name, string Path, bool IsRoot = false);

/// <summary>
/// The subfolders of <see cref="Path"/> on the server (empty <see cref="Path"/>: the drives / top folders), sorted by name,
/// at most <see cref="Oadm.Sdk.Plugins.ServerFolderTexts.MaxEntries"/>; hidden, system and unreadable folders left out.
/// </summary>
/// <param name="Path">The listed folder, normalized; empty for the roots.</param>
/// <param name="Parent">Its parent; null at a root and for the roots.</param>
/// <param name="Folders">The subfolders.</param>
/// <param name="More">There were more subfolders than the list holds.</param>
/// <param name="Problem">Why a plugin may not keep its data in <see cref="Path"/> (<see cref="IServerFolders.CheckAsync"/>); null when it may.</param>
public sealed record ServerFolderListing(string Path, string? Parent, IReadOnlyList<ServerFolder> Folders, bool More, string? Problem);

/// <summary>
/// The folders of the server's computer for a plugin setting such as "Folder on the server" (SDK 0.17.0,
/// <see cref="ICorePluginClientContext.ServerFolders"/>): the client may run on another computer, so a folder of the
/// server is never chosen with the local folder dialog. Administrators only (the server refuses everyone else with
/// PERMISSION_DENIED); names and paths only, never files. Errors arrive as <c>Grpc.Core.RpcException</c> with the text to
/// show (<see cref="ServerErrors.Message"/>). The shared control is <see cref="Controls.ServerFolderPicker"/> with its dialog
/// <see cref="Controls.ServerFolderWindow"/>.
/// </summary>
public interface IServerFolders
{
    /// <summary>The subfolders of <paramref name="path"/>; null or empty: the drives / top folders.</summary>
    Task<ServerFolderListing> ListAsync(string? path, CancellationToken ct);

    /// <summary>
    /// Makes the folder <paramref name="name"/> in <paramref name="parentPath"/> (audited; as the installed service only
    /// administrators / root can change it) and returns it. Check the name first with
    /// <see cref="Oadm.Sdk.Plugins.ServerFolderTexts.CheckName"/>.
    /// </summary>
    Task<ServerFolder> CreateAsync(string parentPath, string name, CancellationToken ct);

    /// <summary>
    /// Null when a plugin may keep its data in <paramref name="path"/>, else why not (one of
    /// <see cref="Oadm.Sdk.Plugins.ServerFolderTexts"/>): the server part checks the same with <c>ICorePluginContext.FolderCheck</c>.
    /// </summary>
    Task<string?> CheckAsync(string path, CancellationToken ct);
}
