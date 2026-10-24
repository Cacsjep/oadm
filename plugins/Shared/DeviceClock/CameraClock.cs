using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DeviceClocks;

/// <summary>A camera's clock as a dialog's read-only query answers it: the camera's time (null: unreadable) and the server's.</summary>
internal sealed record ClockReading(DateTimeOffset? DeviceUtc, DateTimeOffset ServerUtc);

/// <summary>
/// The clock check before a certificate goes onto a camera (docs/spec/pki.md "Clock check before installing a
/// certificate"): every device certificate the OADM CA issues starts <see cref="CertificateStartBack"/> in the past; a camera
/// whose clock is further behind sees it as not valid yet. The dialogs read the clocks (<see cref="QueryMethod"/>), ask
/// with <see cref="Question"/> and pass the answer in the payload (<see cref="ConfirmedField"/>). Linked as source into the
/// PKI and EST server parts; their dialogs use it through them.
/// </summary>
internal static class CameraClock
{
    /// <summary>The read-only query of the dialogs: answers a <see cref="ClockReading"/>.</summary>
    public const string QueryMethod = "readClock";

    /// <summary>The payload field with the user's answer (true: continue although a camera is behind).</summary>
    public const string ConfirmedField = "clockConfirmed";

    public const string TimeServiceId = "time-service";
    public const string TimePath = "axis-cgi/time.cgi";

    /// <summary>Clocks read at once by a dialog.</summary>
    public const int MaxParallel = 8;

    /// <summary>A device certificate starts this long before it is issued.</summary>
    public static readonly TimeSpan CertificateStartBack = TimeSpan.FromMinutes(5);

    /// <summary>A camera still behind the certificate's start by more than this is asked about.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Clocks a dialog reads at most (the first selected cameras), so the question comes quickly also with 30,000 selected;
    /// the tasks check every camera before the first write.
    /// </summary>
    public const int MaxRead = 100;

    /// <summary>How long a dialog waits for one camera's clock.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The device's current UTC time (<c>time.cgi getDateTimeInfo</c>, else the HTTP Date header), or null.</summary>
    public static async Task<DateTimeOffset?> ReadAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(apis);
        var version = apis.FindApi(TimeServiceId, 1)?.Version ?? "1.0";
        var body = new JsonObject { ["apiVersion"] = version, ["context"] = "oadm", ["method"] = "getDateTimeInfo" }.ToJsonString();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(TimePath, UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 200 and < 300)
        {
            try
            {
                var answer = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var text = JsonNode.Parse(answer)?["data"]?["dateTime"]?.GetValue<string>();
                if (text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                {
                    return parsed;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                // fall back to the Date header
            }
        }

        return response.Headers.Date;
    }

    /// <summary>The answer of <see cref="QueryMethod"/>: the camera's clock (cached API list) and the server's right after it.</summary>
    public static async Task<string> QueryAsync(IVapixClient vapix, IDeviceInfo device, TimeProvider time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(time);
        var deviceNow = await ReadAsync(vapix, device.Apis, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new ClockReading(deviceNow, time.GetUtcNow()), Json);
    }

    /// <summary>How far the camera is behind the server (negative: ahead); null when unreadable.</summary>
    public static TimeSpan? Behind(ClockReading reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        return reading.DeviceUtc is { } device ? reading.ServerUtc - device : null;
    }

    /// <summary>How long after its issue a certificate becomes valid on a camera <paramref name="behind"/> the server.</summary>
    public static TimeSpan ValidIn(TimeSpan behind) => behind - CertificateStartBack;

    /// <summary>The camera is still more than <see cref="Tolerance"/> behind the certificate's start: ask first.</summary>
    public static bool NeedsQuestion(TimeSpan behind) => ValidIn(behind) > Tolerance;

    /// <summary>The task's text for a device <paramref name="behind"/> (PKI tasks and EST Set up).</summary>
    public static string Late(TimeSpan behind) =>
        $"The device's time is {Describe(behind)} behind. The certificate becomes valid on the device only in {Describe(ValidIn(behind))}.";

    /// <summary>The task's refusal when the user did not answer the dialog's question with Yes.</summary>
    public static string Refusal(TimeSpan behind) => Late(behind) + " Set the device to use NTP. Nothing was changed.";

    /// <summary>The task's refusal when the device clock cannot be read.</summary>
    public const string Unreadable = "The device clock cannot be read. Nothing was changed.";

    /// <summary>"9 min 12 s", "45 s", "2 h 3 min".</summary>
    public static string Describe(TimeSpan span)
    {
        var d = span.Duration();
        var hours = (int)d.TotalHours;
        return hours > 0 ? (d.Minutes > 0 ? string.Create(CultureInfo.InvariantCulture, $"{hours} h {d.Minutes} min") : string.Create(CultureInfo.InvariantCulture, $"{hours} h"))
            : d.Minutes > 0 ? (d.Seconds > 0 ? string.Create(CultureInfo.InvariantCulture, $"{d.Minutes} min {d.Seconds} s") : string.Create(CultureInfo.InvariantCulture, $"{d.Minutes} min"))
            : string.Create(CultureInfo.InvariantCulture, $"{d.Seconds} s");
    }

    /// <summary>
    /// The question when a camera is behind, null when none is: one camera "The camera's time is 9 min 12 s behind. The
    /// certificate becomes valid on the camera only in 4 min 12 s. Set the camera to use NTP. Continue?"; several: how many
    /// and the largest drift. <paramref name="behind"/>: what each selected camera is behind (unreadable ones left out);
    /// <paramref name="selected"/>: how many cameras were selected.
    /// </summary>
    public static string? Question(IReadOnlyCollection<TimeSpan> behind, int selected)
    {
        ArgumentNullException.ThrowIfNull(behind);
        var late = behind.Where(NeedsQuestion).ToList();
        if (late.Count == 0)
        {
            return null;
        }

        var largest = late.Max();
        if (selected <= 1)
        {
            return $"The camera's time is {Describe(largest)} behind. The certificate becomes valid on the camera only in {Describe(ValidIn(largest))}. Set the camera to use NTP. Continue?";
        }

        var count = string.Create(CultureInfo.InvariantCulture, $"{late.Count:N0} of {selected:N0} cameras");
        return $"The time of {count} is behind, by up to {Describe(largest)}. The certificates become valid on them only in up to {Describe(ValidIn(largest))}. Set the cameras to use NTP. Continue?";
    }

    /// <summary>
    /// Reads the clocks of the first <see cref="MaxRead"/> of <paramref name="deviceIds"/> through the dialog's query
    /// (<see cref="MaxParallel"/> at a time, at most <see cref="ReadTimeout"/> each); returns what each readable camera is behind. Unreadable cameras are left out
    /// (the task checks again where it matters). <paramref name="progress"/> gets the number read so far.
    /// </summary>
    public static async Task<IReadOnlyList<TimeSpan>> ReadBehindAsync(IReadOnlyList<Guid> deviceIds, Func<Guid, CancellationToken, Task<string?>> query,
        IProgress<int>? progress, TimeProvider? time, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        ArgumentNullException.ThrowIfNull(query);
        time ??= TimeProvider.System;
        var count = Sampled(deviceIds.Count);
        var results = new TimeSpan?[count];
        var done = 0;
        await Parallel.ForAsync(0, count, new ParallelOptions { MaxDegreeOfParallelism = MaxParallel, CancellationToken = ct }, async (i, token) =>
        {
            using var timeout = new CancellationTokenSource(ReadTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
            try
            {
                var json = await query(deviceIds[i], linked.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(json) && JsonSerializer.Deserialize<ClockReading>(json, Json) is { } reading)
                {
                    results[i] = Behind(reading);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                // no answer in time: left out
            }
#pragma warning disable CA1031 // An unreadable clock is left out; the task checks it again.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
            }

            progress?.Report(Interlocked.Increment(ref done));
        }).ConfigureAwait(false);
        return [.. results.OfType<TimeSpan>()];
    }

    /// <summary>How many of <paramref name="selected"/> cameras a dialog reads (at most <see cref="MaxRead"/>).</summary>
    public static int Sampled(int selected) => Math.Min(selected, MaxRead);

    /// <summary>The user's answer in a task payload (<see cref="ConfirmedField"/>); false when missing or unreadable.</summary>
    public static bool Confirmed(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return false;
        }

        try
        {
            return JsonNode.Parse(payloadJson) is JsonObject json && json[ConfirmedField] is JsonValue value && value.TryGetValue<bool>(out var confirmed) && confirmed;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The payload with the answer ("{}" or <c>{"clockConfirmed":true}</c>).</summary>
    public static string Payload(bool confirmed) => confirmed ? "{\"" + ConfirmedField + "\":true}" : "{}";
}
