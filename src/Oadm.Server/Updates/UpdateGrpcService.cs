using System.Net;
using System.Net.NetworkInformation;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Contracts.Updates;
using Oadm.Core.Tasks;
using Oadm.Server.Auth;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Updates;

/// <summary>
/// gRPC UpdateService (docs/spec/self-update.md): Get and Start need <c>server.update</c> (Administrator only, AccessPolicy);
/// Watch every signed-in user (the notice "Updating the server to 4.9.0" reaches every client).
/// </summary>
public sealed class UpdateGrpcService(ServerUpdateCoordinator updates, TaskEngine tasks, ILocalAddresses addresses) : Proto.UpdateService.UpdateServiceBase
{
    public override async Task<Proto.UpdateInfo> Get(Proto.Empty request, ServerCallContext context) =>
        await InfoAsync(context, context.CancellationToken).ConfigureAwait(false);

    public override async Task<Proto.UpdateInfo> Start(Proto.StartUpdateRequest request, ServerCallContext context)
    {
        var caller = AuthInterceptor.CallerOf(context);
        try
        {
            await updates.StartAsync(request.Version, caller.Owner, caller.ClientAddress, context.CancellationToken).ConfigureAwait(false);
        }
        catch (UpdateRefusedException ex)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }

        return await InfoAsync(context, CancellationToken.None).ConfigureAwait(false);
    }

    public override async Task Watch(Proto.Empty request, IServerStreamWriter<Proto.UpdateProgress> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(responseStream);
        var same = IsSameComputer(context);
        await foreach (var progress in updates.WatchAsync(context.CancellationToken).ConfigureAwait(false))
        {
            progress.SameComputer = same;
            await responseStream.WriteAsync(progress, context.CancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Proto.UpdateInfo> InfoAsync(ServerCallContext context, CancellationToken ct)
    {
        var environment = updates.Environment;
        var info = new Proto.UpdateInfo
        {
            ServerVersion = environment.ServerVersion,
            Platform = environment.Platform.Name,
            MachineName = System.Environment.MachineName,
            SameComputer = IsSameComputer(context),
            Installed = environment.Installed,
            ActiveTasks = tasks.ActiveCount,
            LatestVersion = (await updates.LatestAsync(ct).ConfigureAwait(false))?.VersionText ?? string.Empty,
            OlderSiteNodes = environment.OlderSiteNodes(),
        };
        if (updates.Current is { } running)
        {
            info.Running = running;
        }

        if (await updates.LastAsync(ct).ConfigureAwait(false) is { } last)
        {
            info.Last = ToProto(last);
        }

        return info;
    }

    public static Proto.UpdateRecord ToProto(UpdateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var proto = new Proto.UpdateRecord
        {
            FromVersion = record.From,
            ToVersion = record.To,
            Started = Timestamp.FromDateTimeOffset(record.StartedUtc),
            Step = record.Step,
            Result = record.Result,
            Reason = record.Reason ?? string.Empty,
            InstallerLog = record.InstallerLog ?? string.Empty,
            DatabaseCopy = record.DatabaseCopy ?? string.Empty,
            RequestedBy = record.RequestedBy,
        };
        if (record.FinishedUtc is { } finished)
        {
            proto.Finished = Timestamp.FromDateTimeOffset(finished);
        }

        return proto;
    }

    /// <summary>
    /// The caller's client runs on this computer: it connects from a loopback address or one of this computer's own
    /// addresses, and names this computer's machine name.
    /// </summary>
    private bool IsSameComputer(ServerCallContext context)
    {
        var caller = AuthInterceptor.CallerOf(context);
        var ip = context.GetHttpContext().Connection.RemoteIpAddress;
        return SameComputer(ip, caller.ClientMachine, System.Environment.MachineName, addresses.Current());
    }

    public static bool SameComputer(IPAddress? remote, string clientMachine, string machineName, IReadOnlyCollection<IPAddress> own)
    {
        ArgumentNullException.ThrowIfNull(own);
        if (remote is null || !string.Equals(clientMachine, machineName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var ip = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
        return IPAddress.IsLoopback(ip) || own.Contains(ip);
    }
}

/// <summary>The unicast addresses of this computer (tests pass their own).</summary>
public interface ILocalAddresses
{
    IReadOnlyCollection<IPAddress> Current();
}

public sealed class SystemLocalAddresses : ILocalAddresses
{
    public IReadOnlyCollection<IPAddress> Current()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address.IsIPv4MappedToIPv6 ? a.Address.MapToIPv4() : a.Address)
                .ToHashSet();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
