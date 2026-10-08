using Microsoft.Extensions.Logging;

using Oadm.Plugins.MetadataMonitor.Parsing;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.MetadataMonitor.Streaming;

/// <summary>
/// One running event stream of one page: reads documents from the server's device event stream, parses them, numbers
/// the messages, pushes them in batches (<see cref="MetadataMonitorOptions.BatchInterval"/>, at most
/// <see cref="MetadataMonitorOptions.MaxBatchMessages"/>; above that the oldest are dropped and counted) and reconnects
/// with backoff after a broken connection. Rejected credentials and a missing event stream end it with Error.
/// </summary>
internal sealed partial class MonitorSession : IAsyncDisposable
{
    private readonly IDeviceEventStreams _streams;
    private readonly IPluginEvents _events;
    private readonly MetadataMonitorOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private readonly Queue<MetadataMessage> _pending = new();
    private IDeviceEventSource? _source;
    private Task _run = Task.CompletedTask;
    private Task _flush = Task.CompletedTask;
    private long _seq;
    private long _messages;
    private long _lostClosed;
    private long _dropped;
    private long _droppedPublished;
    private string _state = MonitorStates.Connecting;
    private string? _text;
    private (long Messages, long Lost) _published = (-1, -1);
    private long _lastKeepAlive;
    private int _stopped;

    public MonitorSession(string id, Guid deviceId, string deviceLabel, IDeviceEventStreams streams, IPluginEvents events, MetadataMonitorOptions options, ILogger logger)
    {
        Id = id;
        DeviceId = deviceId;
        DeviceLabel = deviceLabel;
        _streams = streams;
        _events = events;
        _options = options;
        _logger = logger;
        _lastKeepAlive = options.Time.GetTimestamp();
    }

    public string Id { get; }

    public Guid DeviceId { get; }

    public string DeviceLabel { get; }

    /// <summary>The current state (the same as the last pushed state event).</summary>
    public MonitorState State
    {
        get
        {
            lock (_gate)
            {
                return new MonitorState(Id, DeviceId, _state, _text, _messages, Lost);
            }
        }
    }

    /// <summary>Time since the page last confirmed it is watching.</summary>
    public TimeSpan SinceKeepAlive => _options.Time.GetElapsedTime(Interlocked.Read(ref _lastKeepAlive));

    private long Lost => _lostClosed + _dropped + (_source?.LostDocuments ?? 0);

    public void KeepAlive() => Interlocked.Exchange(ref _lastKeepAlive, _options.Time.GetTimestamp());

    /// <summary>Opens the stream; returns the user message when the device refuses, else starts reading in the background.</summary>
    public async Task<string?> StartAsync(CancellationToken ct)
    {
        SetState(MonitorStates.Connecting, null);
        try
        {
            _source = await _streams.OpenAsync(DeviceId, ct).ConfigureAwait(false);
        }
        catch (DeviceStreamException ex)
        {
            SetState(MonitorStates.Error, ex.Message);
            return ex.Message;
        }
        catch (KeyNotFoundException)
        {
            const string text = "The device is no longer managed by OADM";
            SetState(MonitorStates.Error, text);
            return text;
        }

        LogStarted(_logger, DeviceLabel, Id);
        SetState(MonitorStates.Live, null);
        _run = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        _flush = Task.Run(() => FlushLoopAsync(_cts.Token), CancellationToken.None);
        return null;
    }

    /// <summary>Ends the stream: TEARDOWN, last batch, state Stopped (Error stays Error).</summary>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        await IgnoreCancel(_run).ConfigureAwait(false);
        await IgnoreCancel(_flush).ConfigureAwait(false);
        Flush();
        string state;
        lock (_gate)
        {
            state = _state;
        }

        if (state != MonitorStates.Error)
        {
            SetState(MonitorStates.Stopped, null);
        }

        LogStopped(_logger, DeviceLabel, Id);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    /// <summary>Parses one document and queues its messages (tests call it directly).</summary>
    internal void Add(DeviceMetadataDocument document)
    {
        var parsed = MetadataParser.Parse(document.Xml);
        if (parsed.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var message in parsed)
            {
                _pending.Enqueue(message.WithSeq(++_seq, document.ReceivedUtc));
                _messages++;
                if (_pending.Count > _options.MaxBatchMessages)
                {
                    _pending.Dequeue();
                    _dropped++;
                }
            }
        }
    }

    /// <summary>Pushes the queued messages (in events below the size limit) and the counters when they changed.</summary>
    internal void Flush()
    {
        MetadataMessage[] batch;
        int dropped;
        MonitorState? state = null;
        lock (_gate)
        {
            batch = [.. _pending];
            _pending.Clear();
            dropped = (int)Math.Min(int.MaxValue, _dropped - _droppedPublished);
            _droppedPublished = _dropped;
            var counters = (_messages, Lost);
            if (counters != _published)
            {
                _published = counters;
                state = new MonitorState(Id, DeviceId, _state, _text, _messages, counters.Item2);
            }
        }

        if (batch.Length > 0 || dropped > 0)
        {
            PublishMessages(batch, dropped);
        }

        if (state is not null)
        {
            _events.Publish(MetadataMethods.StateTopic, MetadataJson.Serialize(state));
        }
    }

    private void PublishMessages(MetadataMessage[] batch, int dropped)
    {
        var chunk = new List<MetadataMessage>();
        var size = 0;
        foreach (var message in batch)
        {
            var estimate = 300 + (message.Xml.Length * 2) + message.Info.Length + message.Topic.Length; // JSON escapes
            if (chunk.Count > 0 && size + estimate > _options.MaxEventCharacters)
            {
                _events.Publish(MetadataMethods.MessagesTopic, MetadataJson.Serialize(new MessagesEvent(Id, chunk, dropped)));
                dropped = 0;
                chunk = [];
                size = 0;
            }

            chunk.Add(message);
            size += estimate;
        }

        _events.Publish(MetadataMethods.MessagesTopic, MetadataJson.Serialize(new MessagesEvent(Id, chunk, dropped)));
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.BatchInterval, _options.Time);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            Flush();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await ReadLoopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (_source is { } open)
            {
                await CloseSourceAsync(open).ConfigureAwait(false); // stopped before or between reads
            }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var source = _source!;
            string reason;
            try
            {
                await foreach (var document in source.ReadAsync(ct).ConfigureAwait(false))
                {
                    attempt = 0; // data flows again: the next break starts with the shortest wait
                    Add(document);
                }

                reason = "The device closed the event stream";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await CloseSourceAsync(source).ConfigureAwait(false);
                return;
            }
            catch (DeviceStreamException ex) when (ex.Error is DeviceStreamError.Unauthorized or DeviceStreamError.NotSupported)
            {
                await CloseSourceAsync(source).ConfigureAwait(false);
                SetState(MonitorStates.Error, ex.Message);
                return;
            }
#pragma warning disable CA1031 // Any other failure: reconnect.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                reason = ex.Message;
            }

            await CloseSourceAsync(source).ConfigureAwait(false);
            LogReconnecting(_logger, DeviceLabel, reason);
            if (!await ReconnectAsync(reason, attempt, ct).ConfigureAwait(false))
            {
                return;
            }

            attempt++;
        }
    }

    /// <summary>Waits with backoff and opens again until it works (true), the device refuses for good or the stream stops (false).</summary>
    private async Task<bool> ReconnectAsync(string reason, int attempt, CancellationToken ct)
    {
        try
        {
            while (true)
            {
                SetState(MonitorStates.Reconnecting, reason);
                var delays = _options.ReconnectDelays;
                await Task.Delay(delays[Math.Min(attempt, delays.Count - 1)], _options.Time, ct).ConfigureAwait(false);
                attempt++;
                try
                {
                    var source = await _streams.OpenAsync(DeviceId, ct).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _source = source;
                    }

                    SetState(MonitorStates.Live, null);
                    return true;
                }
                catch (DeviceStreamException ex) when (ex.Error is DeviceStreamError.Unauthorized or DeviceStreamError.NotSupported)
                {
                    SetState(MonitorStates.Error, ex.Message);
                    return false;
                }
                catch (DeviceStreamException ex)
                {
                    reason = ex.Message;
                }
                catch (KeyNotFoundException)
                {
                    SetState(MonitorStates.Error, "The device is no longer managed by OADM");
                    return false;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task CloseSourceAsync(IDeviceEventSource source)
    {
        lock (_gate)
        {
            _lostClosed += source.LostDocuments;
            if (ReferenceEquals(_source, source))
            {
                _source = null;
            }
        }

        try
        {
            await source.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Closing a broken connection may fail; it is gone either way.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogCloseFailed(_logger, ex.Message);
        }
    }

    private void SetState(string state, string? text)
    {
        MonitorState snapshot;
        lock (_gate)
        {
            _state = state;
            _text = text;
            snapshot = new MonitorState(Id, DeviceId, state, text, _messages, Lost);
            _published = (snapshot.Messages, snapshot.Lost);
        }

        _events.Publish(MetadataMethods.StateTopic, MetadataJson.Serialize(snapshot));
    }

    private static async Task IgnoreCancel(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Metadata Monitor: event stream of {Device} started ({StreamId})")]
    private static partial void LogStarted(ILogger logger, string device, string streamId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Metadata Monitor: event stream of {Device} stopped ({StreamId})")]
    private static partial void LogStopped(ILogger logger, string device, string streamId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Metadata Monitor: event stream of {Device} broke ({Reason}), reconnecting")]
    private static partial void LogReconnecting(ILogger logger, string device, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Metadata Monitor: closing the event stream failed: {Reason}")]
    private static partial void LogCloseFailed(ILogger logger, string reason);
}
