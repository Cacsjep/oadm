using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Plugins.Shared;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Ocr;

/// <summary>
/// The page backend (<see cref="OcrMethods"/>). Device groups (docs/spec/device-scope.md): every method works on the
/// readers the caller's device scope owns (users with all devices: every reader); another scope's reader answers like an
/// unknown id; a save takes only a camera of the caller's and the owner's devices; Read now and the last reading need the
/// reader's camera in the caller's devices. The work on the cameras runs without the call (<see cref="ReaderRuntime"/>).
/// </summary>
public sealed partial class OcrPlugin
{
    /// <summary>A user's call.</summary>
    public async Task<string?> InvokeAsync(ICallContext caller, string method, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(caller);
        var ctx = _ctx ?? throw new InvalidOperationException(NotRunning);
        var store = _store ?? throw new InvalidOperationException(NotRunning);
        var runtime = _runtime ?? throw new InvalidOperationException(NotRunning);
        switch (method)
        {
            case OcrMethods.ListReaders:
                return OcrJson.Serialize(ListOf(store.Visible(caller), runtime));
            case OcrMethods.GetReader:
                return OcrJson.Serialize(Visible(store, caller, payloadJson) with { OwnerScopeKey = null });
            case OcrMethods.SaveReader:
            {
                var incoming = OcrJson.Deserialize<SaveReaderRequest>(payloadJson).Reader ?? throw new ArgumentException("Missing payload.", nameof(payloadJson));
                var (saved, before) = await store.SaveAsync(ctx, caller, incoming, runtime.OnGpu, ctx.System.Devices.FindAsync, ct).ConfigureAwait(false);
                if (before is not null && !SamePicture(before, saved))
                {
                    runtime.Forget(saved.Id);
                }

                await runtime.SyncAsync(store.All).ConfigureAwait(false);
                EventTypesChanged?.Invoke(this, EventArgs.Empty);
                PublishReaders(saved.OwnerScopeKey, before?.OwnerScopeKey);
                return OcrJson.Serialize(saved with { OwnerScopeKey = null });
            }

            case OcrMethods.DeleteReader:
            {
                var id = OcrJson.Deserialize<ReaderRequest>(payloadJson).ReaderId;
                if (await store.DeleteAsync(caller, id, ct).ConfigureAwait(false) is { } deleted)
                {
                    await runtime.SyncAsync(store.All).ConfigureAwait(false);
                    runtime.Remove(deleted.Id);
                    EventTypesChanged?.Invoke(this, EventArgs.Empty);
                    PublishReaders(deleted.OwnerScopeKey);
                }

                return "{}";
            }

            case OcrMethods.ReadNow:
            {
                var reader = Visible(store, caller, payloadJson);
                await CallDevices.RequireAsync(caller, [reader.DeviceId], ct).ConfigureAwait(false);
                return OcrJson.Serialize(ReplyOf(await runtime.ReadOnceAsync(reader, ct).ConfigureAwait(false)));
            }

            case OcrMethods.GetReading:
            {
                var reader = Visible(store, caller, payloadJson);
                await CallDevices.RequireAsync(caller, [reader.DeviceId], ct).ConfigureAwait(false);
                return OcrJson.Serialize(runtime.Last(reader.Id) is { } last ? ReplyOf(last) : new ReadingReply());
            }

            default:
                throw new ArgumentException($"Unknown method '{method}'.", nameof(method));
        }
    }

    /// <summary>The reader of the request if the caller sees it; else <see cref="ReaderRules.Gone"/>, the same for an unknown id.</summary>
    private static Reader Visible(ReaderStore store, ICallContext caller, string? payloadJson) =>
        store.FindVisible(caller, OcrJson.Deserialize<ReaderRequest>(payloadJson).ReaderId) ?? throw new ArgumentException(ReaderRules.Gone);

    private static ReadingReply ReplyOf(ReaderReading reading) =>
        new() { Reading = reading.Reading, Jpeg = reading.Jpeg.ToArray(), Details = reading.Details };

    private static ReaderList ListOf(IEnumerable<Reader> readers, ReaderRuntime runtime) => new()
    {
        Readers = [.. readers.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).Select(r => SummaryOf(r, runtime))],
        FpsChoices = ReaderRules.FpsChoices(runtime.OnGpu),
        MaxWatching = ReaderRules.MaxWatching(runtime.OnGpu),
    };

    private static ReaderSummary SummaryOf(Reader reader, ReaderRuntime runtime)
    {
        var last = runtime.Last(reader.Id)?.Reading;
        return new ReaderSummary
        {
            Id = reader.Id,
            Name = reader.Name,
            DeviceId = reader.DeviceId,
            Mode = reader.Mode,
            LastText = last is { Areas.Count: > 0 } ? last.Areas[0].Text : string.Empty,
            LastTime = last?.Time,
            Problem = runtime.ProblemOf(reader.Id),
        };
    }

    /// <summary>Whether the last reading still fits: the same camera, source, resolution and areas.</summary>
    private static bool SamePicture(Reader before, Reader now) =>
        OcrJson.Serialize(before with { Name = string.Empty, Mode = ReaderMode.OnDemand, Fps = 0 })
        == OcrJson.Serialize(now with { Name = string.Empty, Mode = ReaderMode.OnDemand, Fps = 0 });

    /// <summary>
    /// Tells the open pages the reader list after a change: users with all devices every reader, the users of each group
    /// among <paramref name="owners"/> their group's readers.
    /// </summary>
    private void PublishReaders(params string?[] owners)
    {
        if (_ctx?.Events is not { } events || _store is not { } store || _runtime is not { } runtime)
        {
            return;
        }

        var all = store.All;
        events.Publish(OcrMethods.ReadersTopic, OcrJson.Serialize(ListOf(all, runtime)), PluginEventAudience.AllDevices);
        foreach (var group in owners.Select(OwnerScope.Of).Where(o => o != ICallContext.AllDevicesScopeKey).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var mine = all.Where(r => string.Equals(OwnerScope.Of(r.OwnerScopeKey), group, StringComparison.OrdinalIgnoreCase));
            events.Publish(OcrMethods.ReadersTopic, OcrJson.Serialize(ListOf(mine, runtime)), PluginEventAudience.DeviceScope(group));
        }
    }
}
