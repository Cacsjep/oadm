using System.Globalization;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;

namespace Oadm.Plugins.Pki.Policies;

/// <summary>
/// The events "Certificate expires soon" and "Certificate expired" and their Any device twins (docs/spec/pki.md "Policies",
/// Expiry warning): PKI's since OADM 4.5, in its event group "Certificates", with the ids of the built-in events of 4.1 so
/// stored event rules keep working. A device's HTTPS certificate (whoever issued it; the end polling reads) expires soon
/// within the days of its Expiry warning policy (<see cref="PolicyStore.ExpiryDaysFor"/>). Every device is checked at the
/// start (the full report the event rules wait for), every minute and after every change of the policies.
/// </summary>
public sealed partial class ExpiryEvents : IAsyncDisposable
{
    /// <summary>The event group of PKI's events.</summary>
    public const string Group = "Certificates";

    /// <summary>The ids of OADM 4.1's built-in events (kept).</summary>
    public const string ExpiresSoon = "oadm.device.certificate-expires-soon";
    public const string Expired = "oadm.device.certificate-expired";
    public const string AnyExpiresSoon = "oadm.device.any-certificate-expires-soon";
    public const string AnyExpired = "oadm.device.any-certificate-expired";

    /// <summary>The value of an Any device event: how many devices have the condition.</summary>
    public const string CountField = "count";

    private static readonly TimeSpan Round = TimeSpan.FromMinutes(1);

    private readonly PolicyStore _store;
    private readonly Func<CancellationToken, Task<IReadOnlyList<IDeviceInfo>>> _devices;
    private readonly IEventPublisher _publisher;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private ITimer? _timer;
    private int _again;
    private bool _reported;
    private (int Soon, int Expired) _counts = (-1, -1);

    public ExpiryEvents(PolicyStore store, Func<CancellationToken, Task<IReadOnlyList<IDeviceInfo>>> devices, IEventPublisher publisher, TimeProvider time, ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The event types (PKI's <c>IEventSource.EventTypes</c>).</summary>
    public static IReadOnlyList<EventTypeInfo> Types { get; } =
    [
        new(ExpiresSoon, "Certificate expires soon", Group, EventKind.Stateful) { Description = "Within the days of its Expiry warning policy." },
        new(Expired, "Certificate expired", Group, EventKind.Stateful),
        Any(AnyExpiresSoon, "Any device: certificate expires soon", "At least one device certificate is within the days of its Expiry warning policy."),
        Any(AnyExpired, "Any device: certificate expired", "At least one device certificate expired."),
    ];

    /// <summary>Rounds that ran (tests).</summary>
    public int Rounds => Volatile.Read(ref _rounds);

    private int _rounds;

    /// <summary>The first round now (the full report), then every minute and after every change of the policies.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        _store.Changed += OnChanged;
        await RunAsync(ct).ConfigureAwait(false);
        _timer = _time.CreateTimer(_ => _ = RunQuietlyAsync(), null, Round, Round);
    }

    private void OnChanged(object? sender, EventArgs e) => _ = RunQuietlyAsync();

    private async Task RunQuietlyAsync()
    {
        try
        {
            await RunAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // stopping
        }
#pragma warning disable CA1031 // The next round tries again; the reason goes to the server log.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(ex);
        }
    }

    /// <summary>One round over every device; a round asked for while one runs runs right after it.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        Volatile.Write(ref _again, 1);
        while (true)
        {
            if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                return; // the running round sees the flag
            }

            try
            {
                while (Interlocked.Exchange(ref _again, 0) == 1)
                {
                    await RunOnceAsync(ct).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }

            // A round asked for after the last check and before the release found the gate taken: run it now, not a minute later.
            if (Volatile.Read(ref _again) == 0)
            {
                return;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var devices = await _devices(ct).ConfigureAwait(false);
        var now = _time.GetUtcNow().UtcDateTime;
        var soon = new List<Guid>();
        var expired = new List<Guid>();
        foreach (var device in devices)
        {
            if (device.CertNotAfterUtc is not { } end)
            {
                continue;
            }

            if (end <= now)
            {
                expired.Add(device.Id);
            }
            else if (end - now <= TimeSpan.FromDays(_store.ExpiryDaysFor(device)))
            {
                soon.Add(device.Id);
            }
        }

        _publisher.ReplaceStates(ExpiresSoon, soon);
        _publisher.ReplaceStates(Expired, expired);
        if (!_reported)
        {
            // The full report of the server-scoped twins (their "ready"); the counts follow as values.
            _publisher.ReplaceStates(AnyExpiresSoon, soon.Count > 0 ? [Guid.Empty] : []);
            _publisher.ReplaceStates(AnyExpired, expired.Count > 0 ? [Guid.Empty] : []);
            _reported = true;
        }

        if (_counts.Soon != soon.Count)
        {
            SetAny(AnyExpiresSoon, soon.Count);
        }

        if (_counts.Expired != expired.Count)
        {
            SetAny(AnyExpired, expired.Count);
        }

        _counts = (soon.Count, expired.Count);
        Interlocked.Increment(ref _rounds);
    }

    private void SetAny(string typeId, int count) =>
        _publisher.SetState(typeId, null, count > 0, count > 0 ? new Dictionary<string, string> { [CountField] = count.ToString(CultureInfo.InvariantCulture) } : null);

    private static EventTypeInfo Any(string id, string name, string description) =>
        new(id, name, Group, EventKind.Stateful, EventScope.Server, [new EventField(CountField, "Devices") { Kind = EventValueKind.Number, Filter = false }])
        {
            Description = description + "\n\nValues:\n• {" + CountField + "}",
        };

    public async ValueTask DisposeAsync()
    {
        _store.Changed -= OnChanged;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_timer is { } timer)
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }

        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _gate.Release();
        _gate.Dispose();
        _stopping.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "PKI: the certificate expiry events failed; tried again in a minute")]
    private partial void LogFailed(Exception ex);
}
