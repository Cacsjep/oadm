using Google.Protobuf;

using Grpc.Core;

using Oadm.Contracts;
using Oadm.Core.Auth;
using Oadm.Server.AddOns;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Plugins;

/// <summary>
/// The add-on methods of PluginService (docs/spec/add-ons.md, rules in <c>AccessPolicy</c>): the Plugins page's card of
/// optional plugins and the client parts. Every call goes to <see cref="IAddOnInstaller"/>.
/// </summary>
public sealed partial class PluginGrpcService
{
    public override async Task<Proto.AddOnList> ListAddOns(Proto.ListAddOnsRequest request, ServerCallContext context)
    {
        var list = await AddOnCallAsync(() => _addOns.ListAsync(request.CheckReach, context.CancellationToken)).ConfigureAwait(false);
        return await ToProtoAsync(list, context.CancellationToken).ConfigureAwait(false);
    }

    public override async Task InstallAddOn(Proto.AddOnIdRequest request, IServerStreamWriter<Proto.AddOnProgress> responseStream, ServerCallContext context)
    {
        var entry = AddOnOf(request.Id);

        // Progress goes out in order, one write at a time (a stream takes no parallel writes). A caller that has gone makes
        // the writes fail: they are dropped, the install runs to its end and is audited.
        var writes = Task.CompletedTask;
        var gate = new Lock();
        void Write(Proto.AddOnProgress message)
        {
            lock (gate)
            {
                writes = writes.ContinueWith(
                    async _ =>
                    {
                        try
                        {
                            await responseStream.WriteAsync(message).ConfigureAwait(false);
                        }
#pragma warning disable CA1031 // The caller has gone: its progress is dropped.
                        catch (Exception)
#pragma warning restore CA1031
                        {
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default).Unwrap();
            }
        }

        Task WrittenAsync()
        {
            lock (gate)
            {
                return writes;
            }
        }

        var progress = new SyncProgress<Proto.AddOnProgress>(Write);
        AddOnStatus installed;
        try
        {
            installed = await AddOnCallAsync(() => _addOns.InstallAsync(
                entry.Id,
                new SyncProgress<AddOnProgress>(p => progress.Report(new Proto.AddOnProgress { Step = (Proto.AddOnStep)(int)p.Step, Percent = p.Percent })),
                context.CancellationToken)).ConfigureAwait(false);
        }
        catch (AddOnException ex)
        {
            // Nothing was installed: audited with the reason, then the caller hears it.
            await audit.WriteAsync(AuditActions.AddOnInstallFailed, entry.DisplayName, ex.Message, CancellationToken.None).ConfigureAwait(false);
            Write(new Proto.AddOnProgress { Step = Proto.AddOnStep.Install, Error = ex.Message });
            await WrittenAsync().ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            await audit.WriteAsync(AuditActions.AddOnInstallFailed, entry.DisplayName, ex.Message, CancellationToken.None).ConfigureAwait(false);
            await WrittenAsync().ConfigureAwait(false);
            throw;
        }
        catch (RpcException)
        {
            // Refused before anything happened (busy, updating, installed): nothing to audit.
            await WrittenAsync().ConfigureAwait(false);
            throw;
        }

        // The add-on is installed: audited first, even when the caller has gone meanwhile.
        await audit.WriteAsync(AuditActions.AddOnInstalled, $"{entry.DisplayName} V{installed.Version}", null, CancellationToken.None).ConfigureAwait(false);
        Write(new Proto.AddOnProgress { Step = Proto.AddOnStep.Install, Percent = 100, Done = true });
        await WrittenAsync().ConfigureAwait(false);
    }

    public override async Task<Proto.AddOnList> RemoveAddOn(Proto.AddOnIdRequest request, ServerCallContext context)
    {
        var entry = AddOnOf(request.Id);
        var removed = await AddOnCallAsync(() => _addOns.RemoveAsync(entry.Id, context.CancellationToken)).ConfigureAwait(false);
        await audit.WriteAsync(AuditActions.AddOnRemoved, $"{entry.DisplayName} V{removed.Version}", null, CancellationToken.None).ConfigureAwait(false);
        var list = await AddOnCallAsync(() => _addOns.ListAsync(checkReach: false, CancellationToken.None)).ConfigureAwait(false);
        return await ToProtoAsync(list, CancellationToken.None).ConfigureAwait(false);
    }

    public override async Task<Proto.AddOnClientPart> GetAddOnClientPart(Proto.AddOnIdRequest request, ServerCallContext context)
    {
        var entry = AddOnOf(request.Id);
        var caller = CallerContext.Current?.TokenHash ?? string.Empty;
        var part = await AddOnCallAsync(() => _addOns.GetClientPartAsync(entry.Id, caller, context.CancellationToken)).ConfigureAwait(false);
        var reply = new Proto.AddOnClientPart { Id = part.Id, Version = part.Version, Digest = AddOnDigest(part) };
        // The cached bytes go out as they are (never changed after reading): no copy per call.
        reply.Files.AddRange(part.Files.Select(f => new Proto.AddOnFile
        {
            Path = f.RelativePath,
            Data = UnsafeByteOperations.UnsafeWrap(f.Content),
            Sha256 = f.Sha256,
        }));
        return reply;
    }

    /// <summary>The digest of a client part (<see cref="AddOnLayout.Digest"/>): the client checks its cached copy against it.</summary>
    public static string AddOnDigest(AddOnClientPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return AddOnLayout.Digest(part.Files.Select(f => (f.RelativePath, f.Sha256)));
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
        catch (KeyNotFoundException ex)
        {
            throw GrpcGuard.NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw GrpcGuard.FailedPrecondition(ex.Message);
        }
    }

    /// <summary>The list with the digest of each installed add-on's client part (read once per version, then cached).</summary>
    private async Task<Proto.AddOnList> ToProtoAsync(IReadOnlyList<AddOnStatus> list, CancellationToken ct)
    {
        var reply = new Proto.AddOnList();
        foreach (var s in list)
        {
            var info = new Proto.AddOnInfo
            {
                Id = s.Entry.Id,
                DisplayName = s.Entry.DisplayName,
                Description = s.Entry.Description,
                State = (Proto.AddOnState)(int)s.State,
                Percent = s.Percent,
                Message = s.Message ?? string.Empty,
                Version = s.Version,
                CanInstall = s.CanInstall,
            };
            if (s.State == AddOnState.Installed)
            {
                try
                {
                    info.ClientDigest = AddOnDigest(await _addOns.GetClientPartAsync(s.Entry.Id, string.Empty, ct).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    // Bundled (no folder of its own) or unreadable: no digest, the client keeps what it has.
                }
            }

            reply.AddOns.Add(info);
        }

        return reply;
    }
}
