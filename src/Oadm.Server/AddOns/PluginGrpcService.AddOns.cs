using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Plugins.AddOns;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Plugins;

/// <summary>
/// The add-on methods of PluginService (docs/spec/add-ons.md, rules in <c>AccessPolicy</c>): the Plugins page's Add-ons
/// card and the client parts. Every call goes to <see cref="IAddOnInstaller"/> (<see cref="NotSupportedException"/>:
/// UNIMPLEMENTED).
/// </summary>
public sealed partial class PluginGrpcService
{
    public override async Task<Proto.AddOnList> ListAddOns(Proto.Empty request, ServerCallContext context) =>
        ToProto(await AddOnCallAsync(() => _addOns.ListAsync(context.CancellationToken)).ConfigureAwait(false));

    public override async Task InstallAddOn(Proto.AddOnIdRequest request, IServerStreamWriter<Proto.AddOnProgress> responseStream, ServerCallContext context)
    {
        var entry = AddOnOf(request.Id);

        // Progress goes out in order, one write at a time (a stream takes no parallel writes).
        var writes = Task.CompletedTask;
        var gate = new Lock();
        var progress = new SynchronousProgress<AddOnProgress>(p =>
        {
            lock (gate)
            {
                writes = writes.ContinueWith(
                    _ => responseStream.WriteAsync(new Proto.AddOnProgress { Step = (Proto.AddOnStep)(int)p.Step, Percent = p.Percent }),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default).Unwrap();
            }
        });
        Task WrittenAsync()
        {
            lock (gate)
            {
                return writes;
            }
        }

        AddOnStatus installed;
        try
        {
            installed = await AddOnCallAsync(() => _addOns.InstallAsync(entry.Id, progress, context.CancellationToken)).ConfigureAwait(false);
        }
        catch (AddOnException ex)
        {
            await WrittenAsync().ConfigureAwait(false);
            await responseStream.WriteAsync(new Proto.AddOnProgress { Step = Proto.AddOnStep.Install, Error = ex.Message }).ConfigureAwait(false);
            return;
        }

        await WrittenAsync().ConfigureAwait(false);
        // The add-on is installed: it is audited even when the caller has gone meanwhile.
        await audit.WriteAsync(AuditActions.AddOnInstalled, $"{entry.DisplayName} V{installed.Version}", null, CancellationToken.None).ConfigureAwait(false);
        await responseStream.WriteAsync(new Proto.AddOnProgress { Step = Proto.AddOnStep.Install, Percent = 100, Done = true }).ConfigureAwait(false);
    }

    public override async Task<Proto.AddOnList> RemoveAddOn(Proto.AddOnIdRequest request, ServerCallContext context)
    {
        var entry = AddOnOf(request.Id);
        var removed = await AddOnCallAsync(() => _addOns.RemoveAsync(entry.Id, context.CancellationToken)).ConfigureAwait(false);
        await audit.WriteAsync(AuditActions.AddOnRemoved, $"{entry.DisplayName} V{removed.Version}", null, CancellationToken.None).ConfigureAwait(false);
        return ToProto(await AddOnCallAsync(() => _addOns.ListAsync(CancellationToken.None)).ConfigureAwait(false));
    }

    public override async Task<Proto.AddOnClientPart> GetAddOnClientPart(Proto.AddOnIdRequest request, ServerCallContext context)
    {
        var entry = AddOnOf(request.Id);
        var part = await AddOnCallAsync(() => _addOns.GetClientPartAsync(entry.Id, context.CancellationToken)).ConfigureAwait(false);
        var reply = new Proto.AddOnClientPart { Id = part.Id, Version = part.Version };
        reply.Files.AddRange(part.Files.Select(f => new Proto.AddOnFile
        {
            Path = f.RelativePath,
            Data = Google.Protobuf.ByteString.CopyFrom(f.Content.Span),
            Sha256 = f.Sha256,
        }));
        return reply;
    }

    /// <summary>The catalogue entry of <paramref name="id"/>; INVALID_ARGUMENT without one, NOT_FOUND for an unknown id.</summary>
    private static AddOnEntry AddOnOf(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw GrpcGuard.InvalidArgument("id is required.");
        }

        return AddOnCatalog.Find(id) ?? throw GrpcGuard.NotFound(AddOnTexts.Unknown(id));
    }

    /// <summary>Runs an installer call and maps its refusals to gRPC errors (an <see cref="AddOnException"/> passes on).</summary>
    private static async Task<T> AddOnCallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            throw new RpcException(new Status(StatusCode.Unimplemented, ex.Message));
        }
        catch (KeyNotFoundException ex)
        {
            throw GrpcGuard.NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw GrpcGuard.FailedPrecondition(ex.Message);
        }
    }

    private static Proto.AddOnList ToProto(IReadOnlyList<AddOnStatus> list)
    {
        var reply = new Proto.AddOnList();
        var runtime = AddOns.AddOnInstallerOptions.CurrentRuntimeId();
        reply.AddOns.AddRange(list.Select(s => new Proto.AddOnInfo
        {
            Id = s.Entry.Id,
            DisplayName = s.Entry.DisplayName,
            Description = s.Entry.Description,
            SizeBytes = s.Entry.SizeFor(runtime) ?? 0,
            State = (Proto.AddOnState)(int)s.State,
            Percent = s.Percent,
            Message = s.Message ?? string.Empty,
            Version = s.Version,
            CanInstall = s.CanInstall,
        }));
        return reply;
    }

    /// <summary>An <see cref="IProgress{T}"/> that reports on the caller's thread (no synchronization context).</summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
