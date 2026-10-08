using Grpc.Core;

using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Server.Common;
using Oadm.Server.Mapping;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Settings;

/// <summary>
/// gRPC SettingsService. <c>Set</c> is a partial update: zero or empty fields keep their value.
/// Polling changes apply live; a new listen URL applies after a server restart. The credential
/// list RPCs manage the encrypted <see cref="CredentialListStore"/>; replies carry ids and user names,
/// a password only on the explicit <see cref="RevealCredential"/> (logged without the password).
/// </summary>
public sealed partial class SettingsGrpcService(
    ServerSettingsStore store,
    CredentialListStore credentials,
    ILogger<SettingsGrpcService> logger) : Proto.SettingsService.SettingsServiceBase
{
    public override async Task<Proto.ServerSettings> Get(Proto.Empty request, ServerCallContext context) =>
        Mappers.ToProto(await store.GetServerSettingsAsync(context.CancellationToken).ConfigureAwait(false));

    public override async Task<Proto.ServerSettings> Set(Proto.ServerSettings request, ServerCallContext context)
    {
        var current = await store.GetServerSettingsAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            var saved = await store.SetServerSettingsAsync(Mappers.FromProto(request, current), context.CancellationToken).ConfigureAwait(false);
            return Mappers.ToProto(saved);
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
    }

    public override async Task<Proto.CredentialList> ListCredentials(Proto.Empty request, ServerCallContext context)
    {
        var reply = new Proto.CredentialList();
        reply.Entries.AddRange((await credentials.ListAsync(context.CancellationToken).ConfigureAwait(false)).Select(Mappers.ToProto));
        return reply;
    }

    public override async Task<Proto.CredentialEntry> AddCredential(Proto.AddCredentialRequest request, ServerCallContext context)
    {
        try
        {
            return Mappers.ToProto(await credentials.AddAsync(request.UserName, request.Password, context.CancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, ex.Message));
        }
    }

    public override async Task<Proto.Empty> RemoveCredential(Proto.CredentialEntryId request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.Id, out var id) || !await credentials.RemoveAsync(id, context.CancellationToken).ConfigureAwait(false))
        {
            throw GrpcGuard.NotFound($"Credential '{request.Id}' not found.");
        }

        return new Proto.Empty();
    }

    /// <summary>
    /// The stored password of one credential list entry (Settings page eye / copy button; user decision
    /// 2026-10-08). Device passwords are never returned. Logged as Information without the password.
    /// </summary>
    public override async Task<Proto.RevealedCredential> RevealCredential(Proto.CredentialEntryId request, ServerCallContext context)
    {
        var entry = Guid.TryParse(request.Id, out var id)
            ? await credentials.RevealAsync(id, context.CancellationToken).ConfigureAwait(false)
            : null;
        if (entry is null)
        {
            throw GrpcGuard.NotFound($"Credential '{request.Id}' not found.");
        }

        LogRevealed(logger, entry.UserName);
        return new Proto.RevealedCredential { Password = entry.Password };
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Credential list password of {UserName} revealed")]
    private static partial void LogRevealed(ILogger logger, string userName);
}
