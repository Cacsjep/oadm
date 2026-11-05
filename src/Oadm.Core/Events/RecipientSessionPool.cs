using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Actions;
using Oadm.Sdk.Certificates;

namespace Oadm.Core.Events;

/// <summary>What the pool knows of one recipient's connection.</summary>
public enum RecipientConnectionState
{
    NotUsed = 0,
    Connecting = 1,
    Connected = 2,
    NotConnected = 3,
}

/// <summary>The connection of one recipient, in memory only.</summary>
public sealed record RecipientConnectionStatus(Guid Id, string Name, RecipientConnectionState State, string? Reason);

public sealed class RecipientSessionPoolOptions
{
    /// <summary>The first wait before a reconnect; it doubles up to <see cref="LongestWait"/>.</summary>
    public TimeSpan FirstWait { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan LongestWait { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A session no action used for this long closes.</summary>
    public TimeSpan IdleClose { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>One connection attempt.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>A stuck close never holds up a stop.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// The open connections of the persistent recipients (docs/spec/recipient-sessions.md): one session per recipient id, opened at
/// the first action that needs it, reconnected with a growing wait, closed when unused for 5 minutes, when the recipient changes
/// or goes, and at the stop. Nothing is queued: an action on a session that is not connected fails at once. Thread-safe.
/// </summary>
public sealed partial class RecipientSessionPool : IAsyncDisposable
{
    private readonly EventCatalog _catalog;
    private readonly RecipientStore _recipients;
    private readonly Func<IRecipientCertificates?> _certificates;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly RecipientSessionPoolOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Slot> _slots = [];
    private readonly HashSet<Task> _running = [];
    private bool _stopped;

    public RecipientSessionPool(
        EventCatalog catalog,
        RecipientStore recipients,
        Func<IRecipientCertificates?> certificates,
        TimeProvider time,
        ILoggerFactory? loggers = null,
        RecipientSessionPoolOptions? options = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _recipients = recipients ?? throw new ArgumentNullException(nameof(recipients));
        _certificates = certificates ?? throw new ArgumentNullException(nameof(certificates));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _loggers = loggers ?? NullLoggerFactory.Instance;
        _logger = _loggers.CreateLogger<RecipientSessionPool>();
        _options = options ?? new RecipientSessionPoolOptions();
        _recipients.Changed += OnRecipientChanged;
    }

    /// <summary>A connection changed state. Raised outside the pool's lock, on any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The recipients with a session or one on the way; every other recipient is not used.</summary>
    public IReadOnlyList<RecipientConnectionStatus> Statuses()
    {
        lock (_gate)
        {
            return [.. _slots.Values.Select(s => new RecipientConnectionStatus(s.Id, s.Name, s.State, s.Reason))];
        }
    }

    /// <summary>
    /// The session of a recipient. The first action waits for the first attempt; afterwards an action never waits and
    /// throws <see cref="InvalidOperationException"/> with <see cref="RecipientSessionTexts.NotConnected"/>.
    /// </summary>
    public async Task<object> GetAsync(Guid id, CancellationToken ct)
    {
        while (true)
        {
            var slot = Use(id);
            await slot.FirstAttempt.Task.WaitAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (slot.State == RecipientConnectionState.Connected && slot.Session is { } session)
                {
                    return session;
                }

                if (!slot.Closed)
                {
                    throw new InvalidOperationException(RecipientSessionTexts.NotConnected(slot.Reason));
                }
            }

            // The recipient changed during the wait: the next round opens the new one.
        }
    }

    /// <summary>A recipient's Test: one connection with the values of the form, closed at once. Sends nothing.</summary>
    public async Task<(bool Ok, string Message)> TestAsync(RecipientType type, JsonObject values, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(values);
        if (type.Sessions is null)
        {
            return (false, "This type has no connection.");
        }

        IReadOnlyList<X509Certificate2> loaded = [];
        IRecipientSession? session = null;
        using var timeout = new CancellationTokenSource(_options.ConnectTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            (session, loaded) = await CreateAsync(type, values, linked.Token).ConfigureAwait(false);
            await session.ConnectAsync(linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            return (true, RecipientSessionTexts.Connected);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, TimedOut);
        }
#pragma warning disable CA1031 // Plugin code is untrusted: a failed test is the answer.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            return (false, Reason(ex));
        }
        finally
        {
            await CloseAsync(session, loaded).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] running;
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            foreach (var slot in _slots.Values)
            {
                Close(slot);
            }

            _slots.Clear();
            running = [.. _running];
        }

        _recipients.Changed -= OnRecipientChanged;
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    private Slot Use(Guid id)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopped, this);
            if (!_slots.TryGetValue(id, out var slot))
            {
                slot = new Slot(id) { LastUsed = _time.GetUtcNow() };
                _slots[id] = slot;
                var started = slot;
                var loop = Task.Run(() => RunAsync(started), CancellationToken.None);
                _running.Add(loop);
                _ = loop.ContinueWith(
                    done =>
                    {
                        lock (_gate)
                        {
                            _running.Remove(done);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            slot.LastUsed = _time.GetUtcNow();
            return slot;
        }
    }

    private async Task RunAsync(Slot slot)
    {
        var ct = slot.Stop.Token;
        var wait = _options.FirstWait;
        try
        {
            while (!ct.IsCancellationRequested && !IsIdle(slot))
            {
                Set(slot, RecipientConnectionState.Connecting, null);
                var (ended, connected) = await ConnectedRunAsync(slot, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested || ended is null)
                {
                    break; // closed, or idle
                }

                if (connected)
                {
                    wait = _options.FirstWait; // it worked: the wait starts again
                }

                Set(slot, RecipientConnectionState.NotConnected, ended);
                slot.FirstAttempt.TrySetResult();
                await Task.Delay(wait, _time, ct).ConfigureAwait(false);
                wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, _options.LongestWait.Ticks));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Closed.
        }
#pragma warning disable CA1031 // The loop of one recipient never takes the server down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(ex, slot.Name);
        }
        finally
        {
            Finish(slot);
            slot.Stop.Dispose();
        }
    }

    /// <summary>One session from connect to its end. Returns the reason it ended (null: idle or closed) and whether it was connected.</summary>
    private async Task<(string? Ended, bool Connected)> ConnectedRunAsync(Slot slot, CancellationToken ct)
    {
        IRecipientSession? session = null;
        IReadOnlyList<X509Certificate2> loaded = [];
        var dropped = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var connected = false;
        void OnDropped(object? sender, RecipientDroppedEventArgs e) => dropped.TrySetResult(e.Reason);
        try
        {
            var recipient = await _recipients.GetAsync(slot.Id, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException(RecipientType.NotFoundText);
            SetName(slot, recipient.Name);
            if (_catalog.FindRecipientType(recipient.TypeId) is not { Available: true, Type: { Sessions: not null } type })
            {
                throw new InvalidOperationException("This type of recipient is turned off");
            }

            (session, loaded) = await CreateAsync(type, _recipients.DecryptSettings(recipient), ct).ConfigureAwait(false);
            session.Dropped += OnDropped;
            await ConnectAsync(session, ct).ConfigureAwait(false);
            connected = true;
            lock (_gate)
            {
                slot.Session = session;
            }

            Set(slot, RecipientConnectionState.Connected, null);
            slot.FirstAttempt.TrySetResult();
            LogConnected(slot.Name);
            return (await WaitEndAsync(slot, dropped.Task, ct).ConfigureAwait(false), true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (null, connected);
        }
#pragma warning disable CA1031 // Plugin code is untrusted: a failed attempt is a reason.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return (Reason(ex), connected);
        }
        finally
        {
            lock (_gate)
            {
                slot.Session = null;
            }

            if (session is not null)
            {
                session.Dropped -= OnDropped;
            }

            await CloseAsync(session, loaded).ConfigureAwait(false);
        }
    }

    private async Task ConnectAsync(IRecipientSession session, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(_options.ConnectTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await session.ConnectAsync(linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(TimedOut);
        }
    }

    /// <summary>Until the connection ends (its reason) or nobody used it for the idle time (null).</summary>
    private async Task<string?> WaitEndAsync(Slot slot, Task<string> dropped, CancellationToken ct)
    {
        while (true)
        {
            var left = LastUsed(slot) + _options.IdleClose - _time.GetUtcNow();
            if (left <= TimeSpan.Zero)
            {
                return null;
            }

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var idle = Task.Delay(left, _time, stop.Token);
            var first = await Task.WhenAny(idle, dropped).ConfigureAwait(false);
            await stop.CancelAsync().ConfigureAwait(false);
            if (first == dropped)
            {
                return await dropped.ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
        }
    }

    private bool IsIdle(Slot slot) => _time.GetUtcNow() - LastUsed(slot) >= _options.IdleClose;

    private DateTimeOffset LastUsed(Slot slot)
    {
        lock (_gate)
        {
            return slot.LastUsed;
        }
    }

    /// <summary>A session that is not connected, with its certificates; the caller closes both.</summary>
    private async Task<(IRecipientSession Session, IReadOnlyList<X509Certificate2> Loaded)> CreateAsync(RecipientType type, JsonObject values, CancellationToken ct)
    {
        var settings = ActionSettings.Defaults(type.Fields);
        foreach (var (key, value) in values)
        {
            settings[key] = value?.DeepClone();
        }

        var chains = new Dictionary<string, IReadOnlyList<X509Certificate2>>(StringComparer.Ordinal);
        foreach (var field in type.Fields.Where(f => f.Type is ActionFieldType.ClientCertificate or ActionFieldType.TrustedCertificate))
        {
            if (ActionSettings.IsVisible(field, settings, statefulRule: true) && ActionSettings.Text(settings, field.Key) is { Length: > 0 } id)
            {
                var source = _certificates() ?? throw new InvalidOperationException(RecipientSessionTexts.CertificateGone);
                chains[field.Key] = await source.LoadAsync(id, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(RecipientSessionTexts.CertificateGone);
            }
        }

        using var document = JsonDocument.Parse(settings.ToJsonString());
        var request = new RecipientSessionRequest(document.RootElement.Clone(), chains, _loggers.CreateLogger("Oadm.Recipients." + type.Id));
        return (type.Sessions!.Create(request), [.. chains.Values.SelectMany(c => c)]);
    }

    private async Task CloseAsync(IRecipientSession? session, IReadOnlyList<X509Certificate2> loaded)
    {
        try
        {
            if (session is not null)
            {
                await session.DisposeAsync().AsTask().WaitAsync(_options.CloseTimeout, _time).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // A close that fails or hangs is given up on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCloseFailed(ex);
        }

        foreach (var certificate in loaded)
        {
            certificate.Dispose();
        }
    }

    private void OnRecipientChanged(object? sender, Guid id)
    {
        lock (_gate)
        {
            if (!_slots.Remove(id, out var slot))
            {
                return;
            }

            Close(slot);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Under the lock.</summary>
    private static void Close(Slot slot)
    {
        slot.Closed = true;
        slot.Stop.Cancel();
        slot.FirstAttempt.TrySetResult();
    }

    private void Finish(Slot slot)
    {
        bool removed;
        lock (_gate)
        {
            slot.Closed = true;
            removed = _slots.TryGetValue(slot.Id, out var current) && current == slot && _slots.Remove(slot.Id);
        }

        slot.FirstAttempt.TrySetResult();
        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetName(Slot slot, string name)
    {
        lock (_gate)
        {
            slot.Name = name;
        }
    }

    private void Set(Slot slot, RecipientConnectionState state, string? reason)
    {
        bool changed;
        lock (_gate)
        {
            changed = slot.State != state || !string.Equals(slot.Reason, reason, StringComparison.Ordinal);
            slot.State = state;
            slot.Reason = reason;
        }

        if (changed && state == RecipientConnectionState.NotConnected)
        {
            LogNotConnected(slot.Name, reason ?? string.Empty);
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string Reason(Exception ex) =>
        string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message.Trim();

    /// <summary>The reason of an attempt that got no answer.</summary>
    private const string TimedOut = "Timeout - no answer";

    [LoggerMessage(Level = LogLevel.Information, Message = "Recipient {Name} connected")]
    private partial void LogConnected(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recipient {Name} is not connected: {Reason}")]
    private partial void LogNotConnected(string name, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The connection of recipient {Name} stopped unexpectedly")]
    private partial void LogFailed(Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A recipient connection did not close")]
    private partial void LogCloseFailed(Exception ex);

    private sealed class Slot(Guid id)
    {
        public Guid Id { get; } = id;

        public CancellationTokenSource Stop { get; } = new();

        public TaskCompletionSource FirstAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name { get; set; } = string.Empty;

        public RecipientConnectionState State { get; set; } = RecipientConnectionState.Connecting;

        public string? Reason { get; set; }

        public IRecipientSession? Session { get; set; }

        public DateTimeOffset LastUsed { get; set; }

        public bool Closed { get; set; }
    }
}
