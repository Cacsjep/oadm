using Grpc.Core;

using Oadm.Core.Auth;
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
    AuditLog audit,
    ILogger<SettingsGrpcService> logger) : Proto.SettingsService.SettingsServiceBase
{
    public override async Task<Proto.ServerSettings> Get(Proto.Empty request, ServerCallContext context) =>
        await WithNoticeAsync(Mappers.ToProto(await store.GetServerSettingsAsync(context.CancellationToken).ConfigureAwait(false)), context.CancellationToken).ConfigureAwait(false);

    public override async Task<Proto.ServerSettings> Set(Proto.ServerSettings request, ServerCallContext context)
    {
        var current = await store.GetServerSettingsAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            var saved = await store.SetServerSettingsAsync(Mappers.FromProto(request, current), context.CancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(AuditActions.SettingsChanged, "Server settings", Changes(current, saved), context.CancellationToken).ConfigureAwait(false);
            return await WithNoticeAsync(Mappers.ToProto(saved), context.CancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
    }

    /// <summary>Adds the key replacement notice (production hardening 3) when the server had to replace its master key.</summary>
    private async Task<Proto.ServerSettings> WithNoticeAsync(Proto.ServerSettings settings, CancellationToken ct)
    {
        if (await MasterKeyCheck.ReadNoticeAsync(store, ct).ConfigureAwait(false) is { } notice)
        {
            settings.KeyReplaced = new Proto.KeyReplacedNotice
            {
                Id = notice.Id,
                Message = notice.Message,
                Devices = notice.Devices,
                CredentialListEntries = notice.CredentialListEntries,
                Replaced = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.SpecifyKind(notice.ReplacedUtc, DateTimeKind.Utc)),
            };
        }

        return settings;
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
            var added = await credentials.AddAsync(request.UserName, request.Password, context.CancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(AuditActions.CredentialAdded, added.UserName, null, context.CancellationToken).ConfigureAwait(false);
            return Mappers.ToProto(added);
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
        var entry = Guid.TryParse(request.Id, out var id)
            ? (await credentials.ListAsync(context.CancellationToken).ConfigureAwait(false)).FirstOrDefault(e => e.Id == id)
            : null;
        if (entry is null || !await credentials.RemoveAsync(id, context.CancellationToken).ConfigureAwait(false))
        {
            throw GrpcGuard.NotFound($"Credential '{request.Id}' not found.");
        }

        await audit.WriteAsync(AuditActions.CredentialRemoved, entry.UserName, null, context.CancellationToken).ConfigureAwait(false);
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
        await audit.WriteAsync(AuditActions.CredentialRevealed, entry.UserName, null, context.CancellationToken).ConfigureAwait(false);
        return new Proto.RevealedCredential { Password = entry.Password };
    }

    /// <summary>"Polling interval 60 -> 30 s, Server name a -> b" (no secrets in settings).</summary>
    internal static string Changes(ServerSettings before, ServerSettings after)
    {
        var changes = new List<string>();
        void Add<T>(string name, T a, T b)
        {
            if (!EqualityComparer<T>.Default.Equals(a, b))
            {
                changes.Add(System.FormattableString.Invariant($"{name} {a} -> {b}"));
            }
        }

        Add("Polling interval (s)", before.PollingIntervalSeconds, after.PollingIntervalSeconds);
        Add("Full refresh (min)", before.FullRefreshMinutes, after.FullRefreshMinutes);
        Add("Scan parallelism", before.ScanParallelism, after.ScanParallelism);
        Add("Scan timeout (ms)", before.ScanTimeoutMs, after.ScanTimeoutMs);
        Add("Zero-conf scan (s)", before.ZeroConfSeconds, after.ZeroConfSeconds);
        Add("Parallel tasks per plugin", before.MaxParallelTasksPerPlugin, after.MaxParallelTasksPerPlugin);
        Add("Server name", before.ServerName, after.ServerName);
        Add("Listen URL", before.ListenUrl, after.ListenUrl);
        Add("Use host name", before.UseHostName, after.UseHostName);
        return changes.Count == 0 ? "No change" : string.Join(", ", changes);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Credential list password of {UserName} revealed")]
    private static partial void LogRevealed(ILogger logger, string userName);
}
