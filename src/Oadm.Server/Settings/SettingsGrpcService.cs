using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Server.Auth;
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
    ILogger<SettingsGrpcService> logger,
    Core.Plugins.PluginLicenses licenses,
    ActionDestinations destinations) : Proto.SettingsService.SettingsServiceBase
{
    public override async Task<Proto.ServerSettings> Get(Proto.Empty request, ServerCallContext context)
    {
        var settings = await WithNoticeAsync(Mappers.ToProto(await store.GetServerSettingsAsync(context.CancellationToken).ConfigureAwait(false)), context.CancellationToken).ConfigureAwait(false);
        return await WithDestinationsAsync(settings, context).ConfigureAwait(false);
    }

    /// <summary>
    /// A partial update; <c>action_destinations</c> unset keeps the list (where event rules of device groups may send),
    /// set replaces it (validated: "host" or "host:port", at most 100).
    /// </summary>
    public override async Task<Proto.ServerSettings> Set(Proto.ServerSettings request, ServerCallContext context)
    {
        var current = await store.GetServerSettingsAsync(context.CancellationToken).ConfigureAwait(false);
        try
        {
            string? destinationChange = null;
            if (request.ActionDestinations is { } checkFirst)
            {
                ActionDestinations.Normalize(checkFirst.Entries); // a bad address changes nothing
            }

            var saved = await store.SetServerSettingsAsync(Mappers.FromProto(request, current), context.CancellationToken).ConfigureAwait(false);
            if (request.ActionDestinations is { } list)
            {
                var before = await destinations.ListAsync(context.CancellationToken).ConfigureAwait(false);
                var after = await destinations.SetAsync(list.Entries, context.CancellationToken).ConfigureAwait(false);
                if (!before.SequenceEqual(after, StringComparer.OrdinalIgnoreCase))
                {
                    destinationChange = "Addresses rules of device groups may send to: " + (after.Count == 0 ? "none" : string.Join(", ", after));
                }
            }

            var changes = Changes(current, saved);
            if (destinationChange is not null)
            {
                changes = changes == "No change" ? destinationChange : changes + ", " + destinationChange;
            }

            await audit.WriteAsync(AuditActions.SettingsChanged, "Server settings", changes, context.CancellationToken).ConfigureAwait(false);
            return await WithDestinationsAsync(await WithNoticeAsync(Mappers.ToProto(saved), context.CancellationToken).ConfigureAwait(false), context).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }
    }

    /// <summary>The allowed addresses for users with all devices (they name the VMS and alarm servers); never for a group.</summary>
    private async Task<Proto.ServerSettings> WithDestinationsAsync(Proto.ServerSettings settings, ServerCallContext context)
    {
        if (AuthInterceptor.CallerOf(context).HasAllDevices)
        {
            settings.ActionDestinations = new Proto.ActionDestinationList();
            settings.ActionDestinations.Entries.AddRange(await destinations.ListAsync(context.CancellationToken).ConfigureAwait(false));
        }

        return settings;
    }

    /// <summary>
    /// Adds the key replacement notice (production hardening 3) when the server had to replace its master key, and for the
    /// Administrator role the notice of the upgrade to roles.
    /// </summary>
    private async Task<Proto.ServerSettings> WithNoticeAsync(Proto.ServerSettings settings, CancellationToken ct)
    {
        settings.ServerId = licenses.ServerId;
        if (CallerContext.Current is { IsAdmin: true } && await RolesUpgradeNotice.ReadAsync(store, ct).ConfigureAwait(false) is { } roles)
        {
            settings.RolesNotice = new Proto.RolesUpgradeNotice { Id = roles.Id };
            settings.RolesNotice.OperatorLost.AddRange(roles.OperatorLost);
        }

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
    /// The stored password of one credential list entry (Settings page eye / copy button).
    /// Device passwords are never returned. Logged as Information without the password.
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
        Add("Addresses scanned at once", before.ScanParallelism, after.ScanParallelism);
        Add("Scan timeout (ms)", before.ScanTimeoutMs, after.ScanTimeoutMs);
        Add("Discovery duration (s)", before.ZeroConfSeconds, after.ZeroConfSeconds);
        Add("Parallel tasks per plugin", before.MaxParallelTasksPerPlugin, after.MaxParallelTasksPerPlugin);
        Add("Try waiting devices every (min)", before.WaitingRetryMinutes, after.WaitingRetryMinutes);
        Add("Allow Basic login over HTTP", OnOff(before.AllowBasicOverHttp), OnOff(after.AllowBasicOverHttp));
        Add("Server name", before.ServerName, after.ServerName);
        Add("Listen URL", before.ListenUrl, after.ListenUrl);
        return changes.Count == 0 ? "No change" : string.Join(", ", changes);
    }

    private static string OnOff(bool value) => value ? "On" : "Off";

    [LoggerMessage(Level = LogLevel.Information, Message = "Credential list password of {UserName} revealed")]
    private static partial void LogRevealed(ILogger logger, string userName);
}
