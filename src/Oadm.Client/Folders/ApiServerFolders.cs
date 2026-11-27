using Oadm.Client.Api;
using Oadm.Sdk.Client;

namespace Oadm.Client.Folders;

/// <summary>
/// The server's folders for plugin pages (<see cref="ICorePluginClientContext.ServerFolders"/>) through ServerFolderService;
/// the server allows it to administrators only. Errors stay <c>RpcException</c> with the server's text.
/// </summary>
internal sealed class ApiServerFolders(IOadmApi api) : IServerFolders
{
    public async Task<ServerFolderListing> ListAsync(string? path, CancellationToken ct)
    {
        var reply = await api.ListServerFoldersAsync(path ?? string.Empty, ct).ConfigureAwait(true);
        return new ServerFolderListing(
            reply.Path,
            reply.Parent.Length == 0 ? null : reply.Parent,
            [.. reply.Folders.Select(f => new ServerFolder(f.Name, f.Path, f.IsRoot))],
            reply.More,
            reply.Problem.Length == 0 ? null : reply.Problem);
    }

    public async Task<ServerFolder> CreateAsync(string parentPath, string name, CancellationToken ct)
    {
        var created = await api.CreateServerFolderAsync(parentPath, name, ct).ConfigureAwait(true);
        return new ServerFolder(created.Name, created.Path, created.IsRoot);
    }

    public async Task<string?> CheckAsync(string path, CancellationToken ct)
    {
        var check = await api.CheckServerFolderAsync(path, ct).ConfigureAwait(true);
        return check.Problem.Length == 0 ? null : check.Problem;
    }
}
