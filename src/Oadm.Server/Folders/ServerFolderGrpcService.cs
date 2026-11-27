using Grpc.Core;

using Oadm.Core.Auth;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Folders;

/// <summary>
/// gRPC ServerFolderService: the folders of the server's computer for the client's server folder picker (Administrator
/// only, <see cref="Auth.AccessPolicy"/>); a new folder is audited. See <see cref="ServerFolderBrowser"/>.
/// </summary>
public sealed class ServerFolderGrpcService(ServerFolderBrowser browser, AuditLog audit) : Proto.ServerFolderService.ServerFolderServiceBase
{
    public override async Task<Proto.ServerFolderList> List(Proto.ServerFolderRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var listing = await Run(() => browser.ListAsync(request.Path, context.CancellationToken)).ConfigureAwait(false);
        var reply = new Proto.ServerFolderList
        {
            Path = listing.Path,
            Parent = listing.Parent ?? string.Empty,
            More = listing.More,
            Problem = listing.Problem ?? string.Empty,
        };
        reply.Folders.AddRange(listing.Folders.Select(ToProto));
        return reply;
    }

    public override async Task<Proto.ServerFolder> Create(Proto.CreateServerFolderRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var created = await Run(() => browser.CreateAsync(request.Path, request.Name, context.CancellationToken)).ConfigureAwait(false);
        await audit.WriteAsync(AuditActions.ServerFolderCreated, created.Path, null, context.CancellationToken).ConfigureAwait(false);
        return ToProto(created);
    }

    public override async Task<Proto.ServerFolderCheck> Check(Proto.ServerFolderRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        var problem = await browser.CheckAsync(request.Path, context.CancellationToken).ConfigureAwait(false);
        return new Proto.ServerFolderCheck { Path = ServerFolderBrowser.Normalize(request.Path) ?? string.Empty, Problem = problem ?? string.Empty };
    }

    private static Proto.ServerFolder ToProto(ServerFolderEntry entry) => new() { Name = entry.Name, Path = entry.Path, IsRoot = entry.IsRoot };

    private static async Task<T> Run<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (ServerFolderException ex)
        {
            var code = ex.Refusal switch
            {
                ServerFolderRefusal.Invalid => StatusCode.InvalidArgument,
                ServerFolderRefusal.NotFound => StatusCode.NotFound,
                ServerFolderRefusal.Exists => StatusCode.AlreadyExists,
                _ => StatusCode.FailedPrecondition,
            };
            throw new RpcException(new Status(code, ex.Message));
        }
    }
}
