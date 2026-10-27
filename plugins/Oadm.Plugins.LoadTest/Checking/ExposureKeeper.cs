using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.LoadTest.Checking;

/// <summary>
/// The only write of a check (docs/spec/load-test.md, "The exposure always goes back"): before Low light it reads
/// <c>ImageSource.I&lt;n&gt;.Sensor.ExposureValue</c> of each source (n = camera - 1) through param.cgi, keeps the original in
/// memory only, and writes <see cref="MaxValue"/>; after the phase, on Cancel, when the stream drops out and when the
/// plugin stops it writes the original back. A source without the parameter, or with a value that is not a number from 0
/// to 100, is not written (its Low light result is N/A). A failed restore gives a warning that names the camera and the
/// original value. Every write and restore is logged (parameter and values only).
/// </summary>
internal sealed partial class ExposureKeeper(IVapixClientFactory vapix, ILogger logger, LoadTestOptions options)
{
    /// <summary>The highest exposure value of Axis cameras (the parameter's range is 0 to 100).</summary>
    public const int MaxValue = 100;

    private const string ParamPath = "/axis-cgi/param.cgi";

    private readonly ConcurrentDictionary<string, Kept> _kept = new(StringComparer.Ordinal);

    /// <summary>A written source: the camera's name for the warning and the original value.</summary>
    private sealed record Kept(Guid DeviceId, string Parameter, string CameraName, string Original);

    /// <summary>The parameter of a camera (RTSP camera 1 = <c>ImageSource.I0</c>).</summary>
    public static string ParameterOf(int camera) => string.Create(CultureInfo.InvariantCulture, $"ImageSource.I{camera - 1}.Sensor.ExposureValue");

    /// <summary>The values not written back yet (tests).</summary>
    internal int KeptCount => _kept.Count;

    /// <summary>
    /// Reads and writes the maximum for every stream (<see cref="LoadTestOptions.ExposureParallelism"/> at a time);
    /// returns the stream ids whose exposure is now at the maximum (the others are N/A in Low light).
    /// </summary>
    public async Task<IReadOnlySet<string>> RaiseAsync(IReadOnlyList<PlannedStream> streams, CancellationToken ct)
    {
        var raised = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
        await Parallel.ForEachAsync(
            streams,
            new ParallelOptions { MaxDegreeOfParallelism = options.ExposureParallelism, CancellationToken = ct },
            async (stream, token) =>
            {
                if (await RaiseAsync(stream, token).ConfigureAwait(false))
                {
                    raised[stream.StreamId] = true;
                }
            }).ConfigureAwait(false);
        return raised.Keys.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Writes the original of one stream back (after its Low light, or when it dropped out); null or the warning.</summary>
    public async Task<string?> RestoreAsync(string streamId)
    {
        if (!_kept.TryRemove(streamId, out var kept))
        {
            return null;
        }

        try
        {
            await WriteAsync(kept.DeviceId, kept.Parameter, kept.Original).ConfigureAwait(false);
            LogRestored(logger, kept.CameraName, kept.Parameter, kept.Original);
            return null;
        }
#pragma warning disable CA1031 // A failed restore becomes the run's warning; the other cameras are restored.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRestoreFailed(logger, kept.CameraName, kept.Parameter, kept.Original, ex.Message);
            return RestoreWarning(kept.CameraName, kept.Original);
        }
    }

    /// <summary>Writes every original still kept back; the warnings of the failed ones.</summary>
    public async Task<IReadOnlyList<string>> RestoreAllAsync()
    {
        var warnings = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(
            _kept.Keys.ToList(),
            new ParallelOptions { MaxDegreeOfParallelism = options.ExposureParallelism },
            async (id, _) =>
            {
                if (await RestoreAsync(id).ConfigureAwait(false) is { } warning)
                {
                    warnings.Add(warning);
                }
            }).ConfigureAwait(false);
        return [.. warnings.Order(StringComparer.Ordinal)];
    }

    /// <summary>"Entrance: exposure 50 was not written back".</summary>
    public static string RestoreWarning(string cameraName, string original) => $"{cameraName}: exposure {original} was not written back";

    private async Task<bool> RaiseAsync(PlannedStream stream, CancellationToken ct)
    {
        var parameter = ParameterOf(stream.Source.Camera);
        var name = CheckPlanner.NameOf(stream.Device);
        string? original;
        try
        {
            var client = await vapix.CreateAsync(stream.Device.Id, ct).ConfigureAwait(false);
            var values = await client.ListParametersAsync([parameter], ct).ConfigureAwait(false);
            original = values.TryGetValue(parameter, out var value) ? value.Trim() : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A camera whose value cannot be read is N/A in Low light; the check goes on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogNotRead(logger, name, parameter, ex.Message);
            return false;
        }

        // Validate before writing: only a number in the parameter's range is kept and written back later.
        if (original is null || !int.TryParse(original, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || number is < 0 or > MaxValue)
        {
            LogNoParameter(logger, name, parameter);
            return false;
        }

        // Kept before the write: a write whose answer is lost is still written back.
        _kept[stream.StreamId] = new Kept(stream.Device.Id, parameter, name, original);
        try
        {
            await WriteAsync(stream.Device.Id, parameter, MaxValue.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
            LogRaised(logger, name, parameter, original, MaxValue);
            return true;
        }
#pragma warning disable CA1031 // A refused write: N/A in Low light, and the original goes back.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogWriteFailed(logger, name, parameter, ex.Message);
            return false;
        }
    }

    /// <summary>A param.cgi update in the POST body, with its own timeout (restores run without the run's token).</summary>
    private async Task WriteAsync(Guid deviceId, string parameter, string value)
    {
        using var timeout = new CancellationTokenSource(options.WriteTimeout, options.Time);
        var client = await vapix.CreateAsync(deviceId, timeout.Token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Post, ParamPath)
        {
            Content = new StringContent("action=update&" + Uri.EscapeDataString(parameter) + "=" + Uri.EscapeDataString(value), Encoding.ASCII, "application/x-www-form-urlencoded"),
        };
        using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(Oadm.Sdk.Devices.DeviceMessages.ForHttpStatus((int)response.StatusCode) ?? Oadm.Sdk.Devices.DeviceMessages.ServerError((int)response.StatusCode));
        }

        if (body.TrimStart().StartsWith("# Error", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException(body.Trim());
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Load test: {Camera} {Parameter} set from {Original} to {Value} for Low light")]
    private static partial void LogRaised(ILogger logger, string camera, string parameter, string original, int value);

    [LoggerMessage(Level = LogLevel.Information, Message = "Load test: {Camera} {Parameter} written back to {Original}")]
    private static partial void LogRestored(ILogger logger, string camera, string parameter, string original);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Load test: {Camera} {Parameter} was not written back to {Original}: {Error}")]
    private static partial void LogRestoreFailed(ILogger logger, string camera, string parameter, string original, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Load test: {Camera} has no {Parameter}; Low light N/A")]
    private static partial void LogNoParameter(ILogger logger, string camera, string parameter);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Load test: {Camera} {Parameter} could not be read, Low light N/A: {Error}")]
    private static partial void LogNotRead(ILogger logger, string camera, string parameter, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Load test: {Camera} {Parameter} could not be written, Low light N/A: {Error}")]
    private static partial void LogWriteFailed(ILogger logger, string camera, string parameter, string error);
}
