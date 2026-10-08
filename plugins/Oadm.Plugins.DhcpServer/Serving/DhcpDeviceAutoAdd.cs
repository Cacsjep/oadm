using System.Collections.Concurrent;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.DhcpServer.Protocol;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.DhcpServer.Serving;

/// <summary>Timings and limits of <see cref="DhcpDeviceAutoAdd"/>.</summary>
public sealed record DeviceAutoAddOptions
{
    /// <summary>Devices handled at the same time (each one is a probe, the Axis check and up to 10 logins).</summary>
    public int MaxParallel { get; init; } = 4;

    /// <summary>A device that did not answer yet is tried once more after this time (cameras answer HTTP a few seconds after DHCP).</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Most lease changes waiting; more are dropped (logged) so a flood never grows the memory.</summary>
    public int MaxQueued { get; init; } = 10_000;

    /// <summary>Timeout of one attempt.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Hands the DHCP server's leases of Axis MAC addresses (<see cref="AxisOui"/>) to the server's
/// <see cref="IDeviceAutoAdd"/>, off the receive loop: <see cref="OnLeased"/> never blocks (it only queues). Once per MAC
/// and lease change (the same address again is skipped), at most <see cref="DeviceAutoAddOptions.MaxParallel"/> at a time:
/// a managed device at another address is followed (record moved after the server verified it there); an unmanaged one is
/// added when <c>autoAddEnabled</c> says so. No answer yet (moved device not verified, new device unreachable) is retried
/// once after <see cref="DeviceAutoAddOptions.RetryDelay"/> unless the lease changed meanwhile.
/// </summary>
public sealed partial class DhcpDeviceAutoAdd : IAsyncDisposable
{
    public const string Source = "DHCP server";

    private readonly IDeviceAutoAdd _autoAdd;
    private readonly Func<bool> _autoAddEnabled;
    private readonly DeviceAutoAddOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<Item> _channel = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = false });
    private readonly ConcurrentDictionary<ulong, uint> _handled = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task[] _workers;
    private readonly Lock _peakLock = new();
    private int _pending;
    private int _running;
    private int _retries;

    public DhcpDeviceAutoAdd(IDeviceAutoAdd autoAdd, Func<bool> autoAddEnabled, DeviceAutoAddOptions? options = null, TimeProvider? time = null, ILogger? logger = null)
    {
        _autoAdd = autoAdd ?? throw new ArgumentNullException(nameof(autoAdd));
        _autoAddEnabled = autoAddEnabled ?? throw new ArgumentNullException(nameof(autoAddEnabled));
        _options = options ?? new DeviceAutoAddOptions();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _workers = [.. Enumerable.Range(0, Math.Max(1, _options.MaxParallel)).Select(_ => Task.Run(() => WorkAsync(_cts.Token)))];
    }

    /// <summary>Lease changes queued, running or waiting for their retry (tests).</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Attempts waiting for their retry delay (tests).</summary>
    public int WaitingRetries => Volatile.Read(ref _retries);

    /// <summary>Most attempts that ran at the same time since the start (tests).</summary>
    public int PeakParallel { get; private set; }

    /// <summary>
    /// The engine ACKed <paramref name="address"/> to <paramref name="mac"/> (a new or renewed lease). Returns at once;
    /// false when nothing was queued (not an Axis MAC, same address as last time, queue full).
    /// </summary>
    public bool OnLeased(ulong mac, uint address)
    {
        if (!AxisOui.IsAxis(mac))
        {
            return false;
        }

        if (_handled.TryGetValue(mac, out var last) && last == address)
        {
            return false; // a renewal of the lease already handled
        }

        if (Volatile.Read(ref _pending) >= _options.MaxQueued)
        {
            LogQueueFull(MacAddress.Format(mac));
            return false;
        }

        if (_handled.Count >= _options.MaxQueued)
        {
            _handled.Clear(); // bounded memory; at worst a renewal is handled once more
        }

        _handled[mac] = address;
        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(new Item(mac, address, 0)))
        {
            Interlocked.Decrement(ref _pending);
            return false;
        }

        return true;
    }

    /// <summary>Completes when nothing is queued, running or waiting for a retry (tests).</summary>
    public async Task WhenIdleAsync(CancellationToken ct)
    {
        while (Pending > 0)
        {
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopping
        }

        _cts.Dispose();
    }

    private async Task WorkAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                var retry = false;
                try
                {
                    if (_handled.TryGetValue(item.Mac, out var current) && current == item.Address)
                    {
                        retry = await HandleAsync(item, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // One device's failure never stops the queue (or the DHCP server).
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogFailed(ex, MacAddress.Format(item.Mac), Ip4.Format(item.Address));
                }

                if (retry)
                {
                    _ = RetryLaterAsync(item with { Attempt = item.Attempt + 1 }, ct);
                }
                else
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>One attempt; true = retry later.</summary>
    private async Task<bool> HandleAsync(Item item, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _running);
        lock (_peakLock)
        {
            PeakParallel = Math.Max(PeakParallel, now);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.AttemptTimeout);
            var serial = AxisOui.Serial(item.Mac);
            var address = Ip4.Format(item.Address);
            var follow = await _autoAdd.FollowAsync(serial, address, Source, timeout.Token).ConfigureAwait(false);
            switch (follow)
            {
                case DeviceFollowResult.Moved:
                    LogMoved(serial, address);
                    return false;
                case DeviceFollowResult.NotVerified:
                    LogNotVerified(serial, address, item.Attempt == 0);
                    return item.Attempt == 0;
                case DeviceFollowResult.NotManaged:
                    break;
                default:
                    return false; // managed: same address, or reached by host name
            }

            if (!_autoAddEnabled())
            {
                return false;
            }

            var outcome = await _autoAdd.AddAsync(address, serial, Source, timeout.Token).ConfigureAwait(false);
            LogOutcome(serial, address, outcome.Result, outcome.Message);
            return outcome.Result == DeviceAutoAddResult.Unreachable && item.Attempt == 0;
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private async Task RetryLaterAsync(Item item, CancellationToken ct)
    {
        try
        {
            var delay = Task.Delay(_options.RetryDelay, _time, ct);
            Interlocked.Increment(ref _retries);
            try
            {
                await delay.ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _retries);
            }

            if (_handled.TryGetValue(item.Mac, out var current) && current == item.Address && _channel.Writer.TryWrite(item))
            {
                return; // the worker decrements when done
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }

        Interlocked.Decrement(ref _pending);
    }

    private sealed record Item(ulong Mac, uint Address, int Attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "DHCP: managed device {Serial} got {Address}; OADM follows it there")]
    private partial void LogMoved(string serial, string address);

    [LoggerMessage(Level = LogLevel.Information, Message = "DHCP: managed device {Serial} got {Address} but could not be verified there yet (retry: {Retry})")]
    private partial void LogNotVerified(string serial, string address, bool retry);

    [LoggerMessage(Level = LogLevel.Information, Message = "DHCP: automatic add of {Serial} at {Address}: {Result} - {Message}")]
    private partial void LogOutcome(string serial, string address, DeviceAutoAddResult result, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DHCP: automatic add of {Mac} at {Address} failed")]
    private partial void LogFailed(Exception ex, string mac, string address);

    [LoggerMessage(Level = LogLevel.Warning, Message = "DHCP: too many devices waiting to be added; {Mac} skipped")]
    private partial void LogQueueFull(string mac);
}
