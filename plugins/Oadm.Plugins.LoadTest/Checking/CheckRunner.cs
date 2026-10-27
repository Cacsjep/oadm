using System.Globalization;

using Microsoft.Extensions.Logging;

using Oadm.Sdk.Devices;

namespace Oadm.Plugins.LoadTest.Checking;

/// <summary>
/// Runs one check (docs/spec/load-test.md, The check): batch after batch the phases Ramp25, Ramp50, Ramp75
/// (the first 25 / 50 / 75 % of the batch's streams), Normal (all) and Low light (all, the exposure at its maximum). The
/// streams of a batch stay open from the step they join to the end of the batch. Once per second of
/// <see cref="LoadTestOptions.Time"/> it reads every open probe, takes the difference to the last read and adds one
/// point to the graphs; a stream that sends nothing for <see cref="LoadTestInfo.StallTimeout"/> or does not start
/// within <see cref="LoadTestInfo.StartTimeout"/> is Interrupted for the rest of its phase. At a phase's end each
/// stream passes or fails against the limits of <see cref="LoadTestInfo"/>. Cancel and the plugin's stop end the
/// run at once: the exposure goes back, the probes close and the measured part is kept.
/// </summary>
internal sealed partial class CheckRunner : IAsyncDisposable
{
    private readonly CheckRun _run;
    private readonly IDeviceStreamProbes _probes;
    private readonly ExposureKeeper _exposure;
    private readonly RunStore _store;
    private readonly ILogger _logger;
    private readonly Action<CheckRun> _publish;
    private readonly PeriodicTimer _timer;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _batchCts;
    private RunState _endAs = RunState.Cancelled;
    private string? _endVerdict;
    private int _ticks;
    private int _waitingAfter = -1;
    private int _secondInPhase;
    private int _savedBatch;

    public CheckRunner(CheckRun run, IDeviceStreamProbes probes, ExposureKeeper exposure, RunStore store, LoadTestOptions options, ILogger logger, Action<CheckRun> publish)
    {
        _run = run;
        _probes = probes;
        _exposure = exposure;
        _store = store;
        _logger = logger;
        _publish = publish;
        _time = options.Time;

        // Created before Start returns: a second of the clock that passes before the loop waits is not lost.
        _timer = new PeriodicTimer(TimeSpan.FromSeconds(1), options.Time);
    }

    public CheckRun Run => _run;

    /// <summary>Seconds processed so far (tests wait for it after advancing the clock).</summary>
    public int Ticks => Volatile.Read(ref _ticks);

    /// <summary>
    /// The seconds processed when the loop last began to wait for the next second (-1 before the first wait): tests advance the
    /// clock only once the loop waits, so a phase change never races with the clock.
    /// </summary>
    public int WaitingAfter => Volatile.Read(ref _waitingAfter);

    /// <summary>Streams whose open has not completed (tests wait until only deliberately slow opens are left).</summary>
    public int PendingOpens
    {
        get
        {
            lock (_run.Gate)
            {
                return _run.Streams.Count(s => s.Opening is { IsCompleted: false });
            }
        }
    }

    /// <summary>Completes when the run ended and was saved.</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    public void Start() => Completion = Detached.Run(LoopAsync);

    /// <summary>Ends the run: <paramref name="endAs"/> Cancelled (the user) or Failed with <paramref name="verdict"/> (plugin stop).</summary>
    public void Stop(RunState endAs, string? verdict = null)
    {
        lock (_run.Gate)
        {
            if (_cts.IsCancellationRequested)
            {
                return;
            }

            _endAs = endAs;
            _endVerdict = verdict;
        }

        _cts.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Stop(RunState.Failed, Verdicts.PluginStopped);
        await Completion.ConfigureAwait(false);
        _timer.Dispose();
        _batchCts?.Dispose();
        _cts.Dispose();
    }

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        var batches = _run.Plan.BatchList;
        LogStarted(_logger, _run.StartedBy, _run.Streams.Count, batches.Count, _run.PhaseSeconds);
        var batchIndex = 0;
        try
        {
            for (; batchIndex < batches.Count; batchIndex++)
            {
                var streams = batches[batchIndex].Select(p => _run.ById[p.StreamId]).ToList();
                lock (_run.Gate)
                {
                    _run.Batch = batchIndex + 1;
                    _run.BatchPhases.Clear();
                }

                _batchCts?.Dispose();
                _batchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                foreach (var phase in CheckRun.AllPhases)
                {
                    await BeginPhaseAsync(streams, phase, ct).ConfigureAwait(false);
                    for (_secondInPhase = 0; _secondInPhase < _run.PhaseSeconds;)
                    {
                        Volatile.Write(ref _waitingAfter, Ticks);
                        if (!await _timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                        {
                            throw new OperationCanceledException(ct);
                        }

                        List<StreamState> dropped;
                        lock (_run.Gate)
                        {
                            dropped = Sample(streams, phase);
                            _secondInPhase++;
                            if (_secondInPhase == _run.PhaseSeconds)
                            {
                                EndPhase(streams, phase, partial: false);
                            }
                        }

                        await RestoreDroppedAsync(dropped).ConfigureAwait(false);
                        _publish(_run);
                        Interlocked.Increment(ref _ticks);
                    }

                    if (phase == CheckPhase.LowLight)
                    {
                        await RestoreAllAsync().ConfigureAwait(false);
                    }
                }

                await CloseAsync(streams).ConfigureAwait(false);
                await SaveBatchAsync(batchIndex + 1).ConfigureAwait(false);
                await SaveAsync().ConfigureAwait(false);
            }

            lock (_run.Gate)
            {
                _run.State = RunState.Completed;
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            End(batches, batchIndex, _endAs, _endVerdict);
        }
#pragma warning disable CA1031 // Whatever broke the run, the exposure goes back and the measured part is kept.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogBroke(_logger, ex.Message);
            End(batches, batchIndex, RunState.Failed, "The check stopped: " + ex.Message.TrimEnd('.'));
        }
        finally
        {
            if (batchIndex < batches.Count)
            {
                await CloseAsync([.. batches[batchIndex].Select(p => _run.ById[p.StreamId])]).ConfigureAwait(false);
            }

            await RestoreAllAsync().ConfigureAwait(false);
            if (batchIndex < batches.Count)
            {
                await SaveBatchAsync(batchIndex + 1).ConfigureAwait(false);
            }

            lock (_run.Gate)
            {
                _run.EndedUtc = _time.GetUtcNow();
                foreach (var stream in _run.Streams.Where(s => s.Active))
                {
                    stream.Active = false;
                }
            }

            await SaveAsync().ConfigureAwait(false);
            _publish(_run);
            string verdict;
            lock (_run.Gate)
            {
                verdict = _run.Summary().Verdict;
            }

            LogEnded(_logger, _run.State, verdict);
        }
    }

    /// <summary>The run ended early: the phase in progress keeps what it measured.</summary>
    private void End(IReadOnlyList<IReadOnlyList<PlannedStream>> batches, int batchIndex, RunState state, string? verdict)
    {
        lock (_run.Gate)
        {
            if (batchIndex < batches.Count)
            {
                var streams = batches[batchIndex].Select(p => _run.ById[p.StreamId]).ToList();
                EndPhase(streams, _run.Phase, partial: true);
            }

            _run.State = state;
            _run.FailedVerdict = state == RunState.Failed ? verdict : null;
        }
    }

    private async Task BeginPhaseAsync(List<StreamState> streams, CheckPhase phase, CancellationToken ct)
    {
        IReadOnlySet<string>? raised = null;
        if (phase == CheckPhase.LowLight)
        {
            lock (_run.Gate)
            {
                _run.Phase = phase;
            }

            raised = await _exposure.RaiseAsync([.. streams.Select(s => s.Planned)], ct).ConfigureAwait(false);
        }

        var count = phase switch
        {
            CheckPhase.Ramp25 => (int)Math.Ceiling(streams.Count * 0.25),
            CheckPhase.Ramp50 => (int)Math.Ceiling(streams.Count * 0.5),
            CheckPhase.Ramp75 => (int)Math.Ceiling(streams.Count * 0.75),
            _ => streams.Count,
        };
        lock (_run.Gate)
        {
            _run.Phase = phase;
            _secondInPhase = 0;
            for (var i = 0; i < streams.Count; i++)
            {
                var stream = streams[i];
                var active = i < count;
                stream.Active = active;
                stream.WaitingSeconds = 0;
                stream.SilentSeconds = 0;
                stream.Phases[(int)phase].Result = active ? PhaseResult.Running : PhaseResult.NotApplicable;
                if (raised is not null)
                {
                    stream.ExposureRaised = raised.Contains(stream.Planned.StreamId);
                }

                if (active && stream.Probe is null && stream.Opening is null)
                {
                    Open(stream, _batchCts?.Token ?? ct);
                }

                _run.MarkChanged(stream);
            }
        }
    }

    private void Open(StreamState stream, CancellationToken ct)
    {
        var planned = stream.Planned;
        var request = new StreamProbeRequest(planned.Source.Camera, planned.Source.Width, planned.Source.Height, planned.Source.MaxFps, planned.Codec, PreferUdp: true);
        stream.Opening = Detached.Run(() => _probes.OpenAsync(planned.Device.Id, request, ct));
    }

    /// <summary>One second: reads every stream of the phase, adds its values and one graph point; the streams that dropped out.</summary>
    private List<StreamState> Sample(IReadOnlyList<StreamState> streams, CheckPhase phase)
    {
        var dropped = new List<StreamState>();
        double total = 0, lossSum = 0, worst = 0, jitterSum = 0;
        var measured = 0;
        var startLimit = (int)LoadTestInfo.StartTimeout.TotalSeconds;
        var stallLimit = (int)LoadTestInfo.StallTimeout.TotalSeconds;
        foreach (var stream in streams)
        {
            float mbps = 0, loss = 0, jitter = 0;
            var acc = stream.Phases[(int)phase];
            if (stream.Active && acc.Interrupted && stream.Probe is { Completion.IsCompleted: false } stalled)
            {
                // Interrupted for the rest of the phase: read on, so the next phase starts from now.
                stream.Last = stalled.Read();
            }
            else if (stream.Active && !acc.Interrupted)
            {
                if (stream.Probe is null && stream.Opening is { IsCompleted: true } opening)
                {
                    stream.Opening = null;
                    if (opening.IsCompletedSuccessfully)
                    {
                        var probe = opening.Result;
                        stream.Probe = probe;
                        stream.Last = default;
                        stream.UdpBlocked |= probe.UdpBlocked;
                        stream.Transport = probe.Transport == StreamProbeTransport.Udp && !probe.UdpBlocked ? "UDP" : "TCP";
                        stream.StreamText = StreamState.Describe(probe.Codec, probe.Width, probe.Height, probe.Fps);
                    }
                    else
                    {
                        Interrupt(acc, "Did not start: " + Message(opening.Exception));
                        dropped.Add(stream);
                    }
                }

                if (acc.Interrupted)
                {
                    // dropped above
                }
                else if (stream.Probe is not { } probe)
                {
                    if (++stream.WaitingSeconds >= startLimit)
                    {
                        Interrupt(acc, NotStarted);
                    }
                }
                else if (probe.Completion.IsCompleted)
                {
                    Interrupt(acc, "Stopped sending: " + Message(probe.Completion.Exception));
                    stream.Probe = null;
                    _ = Detached.Run(async () => await probe.DisposeAsync().ConfigureAwait(false));
                    dropped.Add(stream);
                }
                else
                {
                    var now = probe.Read();
                    var last = stream.Last;
                    stream.Last = now;
                    if (now.Packets == 0)
                    {
                        if (++stream.WaitingSeconds >= startLimit)
                        {
                            Interrupt(acc, NotStarted);
                        }
                    }
                    else
                    {
                        var packets = now.Packets - last.Packets;
                        var lost = now.LostPackets - last.LostPackets;
                        var frames = now.Frames - last.Frames;
                        var expected = now.ExpectedFrames - last.ExpectedFrames;
                        var bytes = now.Bytes - last.Bytes;
                        acc.Packets += packets;
                        acc.Lost += lost;
                        acc.Frames += frames;
                        acc.Expected += expected;
                        acc.Late += now.LateFrames - last.LateFrames;
                        acc.Bytes += bytes;
                        acc.JitterSum += now.JitterMs;
                        acc.Seconds++;
                        stream.SilentSeconds = packets == 0 ? stream.SilentSeconds + 1 : 0;
                        if (stream.SilentSeconds >= stallLimit)
                        {
                            Interrupt(acc, string.Create(CultureInfo.InvariantCulture, $"Stopped sending for {stallLimit} s"));
                        }

                        var frameLoss = expected > 0 ? 100.0 * Math.Max(0, expected - frames) / expected : 0;
                        var value = stream.Udp ? (packets + lost > 0 ? 100.0 * lost / (packets + lost) : 0) : frameLoss;
                        mbps = (float)(bytes * 8 / 1_000_000.0);
                        loss = (float)value;
                        jitter = (float)now.JitterMs;
                        total += mbps;
                        lossSum += value;
                        worst = Math.Max(worst, value);
                        jitterSum += now.JitterMs;
                        measured++;
                    }
                }

                _run.MarkChanged(stream);
            }

            stream.Mbps.Add(MathF.Round(mbps, 3));
            stream.Loss.Add(MathF.Round(loss, 3));
            stream.Jitter.Add(MathF.Round(jitter, 2));
        }

        _run.AddPoint(phase, total, measured == 0 ? 0 : lossSum / measured, worst, measured == 0 ? 0 : jitterSum / measured);
        return dropped;
    }

    /// <summary>"Did not start in 10 s".</summary>
    private static readonly string NotStarted = string.Create(CultureInfo.InvariantCulture, $"Did not start in {LoadTestInfo.StartTimeout.TotalSeconds:0} s");

    private static void Interrupt(PhaseAcc acc, string problem)
    {
        acc.Interrupted = true;
        acc.Problem = problem;
    }

    private static string Message(Exception? ex)
    {
        var inner = ex is AggregateException { InnerExceptions.Count: 1 } aggregate ? aggregate.InnerExceptions[0] : ex;
        return (inner?.Message ?? "the stream ended").Trim().TrimEnd('.');
    }

    /// <summary>The results of the phase's streams against the limits, and its step (bar graph).</summary>
    private void EndPhase(IReadOnlyList<StreamState> streams, CheckPhase phase, bool partial)
    {
        var active = streams.Where(s => s.Phases[(int)phase].Result == PhaseResult.Running).ToList();
        if (active.Count == 0)
        {
            return;
        }

        var failed = 0;
        double lossSum = 0;
        var lossCount = 0;
        foreach (var stream in active)
        {
            var acc = stream.Phases[(int)phase];
            if (phase == CheckPhase.LowLight && !stream.ExposureRaised)
            {
                acc.Result = PhaseResult.NotApplicable;
                acc.Problem = null;
            }
            else if (acc.Interrupted)
            {
                acc.Result = PhaseResult.Interrupted;
            }
            else if (acc.Seconds == 0)
            {
                // A cancelled phase that ended before this stream sent anything says nothing about it.
                acc.Result = partial ? PhaseResult.Pending : PhaseResult.Interrupted;
                acc.Problem = partial ? null : NotStarted;
            }
            else
            {
                (acc.Result, acc.Problem) = Judge(acc, stream.Udp);
            }

            if (acc.Result is PhaseResult.Failed or PhaseResult.Interrupted)
            {
                failed++;
            }

            if (acc.Seconds > 0)
            {
                lossSum += acc.PacketLossPercent(stream.Udp) ?? acc.FrameLossPercent;
                lossCount++;
            }

            _run.MarkChanged(stream);
        }

        if (!partial || _secondInPhase > 0)
        {
            _run.AddStep(new StepResult(phase, active.Count, Math.Round(lossCount == 0 ? 0 : lossSum / lossCount, 3), failed));
        }
    }

    /// <summary>Pass: average packet loss, frame loss and late frames each below their limit.</summary>
    internal static (PhaseResult Result, string? Problem) Judge(PhaseAcc acc, bool udp)
    {
        if (acc.PacketLossPercent(udp) is { } packetLoss && packetLoss >= LoadTestInfo.MaxPacketLossPercent)
        {
            return (PhaseResult.Failed, string.Create(CultureInfo.InvariantCulture, $"Packet loss {packetLoss:0.0#} %"));
        }

        if (acc.FrameLossPercent >= LoadTestInfo.MaxFrameLossPercent)
        {
            // Over UDP without packet loss the network delivered everything: the camera itself sent fewer frames (e.g. two
            // 1080p 60 fps streams of one P3265-V give about 38 fps each, measured 2026-10-27).
            return udp && acc.Seconds > 0
                ? (PhaseResult.Failed, string.Create(CultureInfo.InvariantCulture,
                    $"The camera sends {(double)acc.Frames / acc.Seconds:0.#} of {(double)acc.Expected / acc.Seconds:0.#} frames per second"))
                : (PhaseResult.Failed, string.Create(CultureInfo.InvariantCulture, $"Frame loss {acc.FrameLossPercent:0.0#} %"));
        }

        return acc.LatePercent >= LoadTestInfo.MaxLateFramePercent
            ? (PhaseResult.Failed, string.Create(CultureInfo.InvariantCulture, $"Late frames {acc.LatePercent:0.0#} %"))
            : (PhaseResult.Passed, null);
    }

    private async Task RestoreDroppedAsync(List<StreamState> dropped)
    {
        foreach (var stream in dropped)
        {
            if (await _exposure.RestoreAsync(stream.Planned.StreamId).ConfigureAwait(false) is { } warning)
            {
                lock (_run.Gate)
                {
                    _run.AddRestoreWarnings([warning]);
                }
            }
        }
    }

    private async Task RestoreAllAsync()
    {
        var warnings = await _exposure.RestoreAllAsync().ConfigureAwait(false);
        if (warnings.Count > 0)
        {
            lock (_run.Gate)
            {
                _run.AddRestoreWarnings(warnings);
            }
        }
    }

    /// <summary>
    /// Closes the batch's probes (RTSP TEARDOWN). Opens still pending are cancelled and not waited for; one that completes
    /// anyway is closed as soon as it does.
    /// </summary>
    private async Task CloseAsync(IReadOnlyList<StreamState> streams)
    {
        if (_batchCts is { } batch)
        {
            await batch.CancelAsync().ConfigureAwait(false);
        }

        var closing = new List<Task>();
        foreach (var stream in streams)
        {
            if (stream.Probe is { } probe)
            {
                stream.Probe = null;
                closing.Add(probe.DisposeAsync().AsTask());
            }

            if (stream.Opening is { } opening)
            {
                stream.Opening = null;
                _ = opening.ContinueWith(
                    t => t.Result.DisposeAsync().AsTask(),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnRanToCompletion,
                    TaskScheduler.Default).Unwrap();
            }
        }

        try
        {
            await Task.WhenAll(closing).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A probe that fails to close is gone anyway.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Saves the per-second values of a batch and frees them (30,000 streams never sit in memory at once).</summary>
    private async Task SaveBatchAsync(int batch)
    {
        if (batch <= _savedBatch)
        {
            return;
        }

        BatchSeries series;
        lock (_run.Gate)
        {
            var streams = _run.Plan.BatchList[batch - 1].Select(p => _run.ById[p.StreamId]).ToList();
            series = new BatchSeries(
                [.. _run.BatchPhases],
                streams.ToDictionary(s => s.Planned.StreamId, s => new StreamSeries([.. s.Mbps], [.. s.Loss], [.. s.Jitter]), StringComparer.Ordinal));
            foreach (var stream in streams)
            {
                stream.Mbps.Clear();
                stream.Loss.Clear();
                stream.Jitter.Clear();
            }

            _savedBatch = batch;
        }

        await TrySaveAsync(() => _store.SaveBatchAsync(_run.Id, batch, series, CancellationToken.None)).ConfigureAwait(false);
    }

    private async Task SaveAsync()
    {
        StoredRun stored;
        RunDetailData detail;
        lock (_run.Gate)
        {
            stored = new StoredRun(_run.Summary(), _run.Owner);
            detail = new RunDetailData(_run.Graphs(), [.. _run.Streams.Select(s => s.Row())]);
        }

        await TrySaveAsync(() => _store.SaveDetailAsync(_run.Id, detail, CancellationToken.None)).ConfigureAwait(false);
        await TrySaveAsync(() => _store.SaveSummaryAsync(stored, CancellationToken.None)).ConfigureAwait(false);
    }

    private async Task TrySaveAsync(Func<Task> save)
    {
        try
        {
            await save().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A full disk must not keep the exposure from going back; the run stays in memory.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogNotSaved(_logger, _run.Id, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Load test started by {User}: {Streams} camera streams in {Batches} batches, {Seconds} s per step")]
    private static partial void LogStarted(ILogger logger, string user, int streams, int batches, int seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Load test ended ({State}): {Verdict}")]
    private static partial void LogEnded(ILogger logger, RunState state, string verdict);

    [LoggerMessage(Level = LogLevel.Error, Message = "Load test broke off: {Error}")]
    private static partial void LogBroke(ILogger logger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Load test {RunId} could not be saved: {Error}")]
    private static partial void LogNotSaved(ILogger logger, Guid runId, string error);
}

/// <summary>
/// Background work started from a user's call: it runs without the call's context (the host refuses the plugin's full
/// device access in the call of a user with a device group, and work started from the call keeps the call). The check's
/// devices were checked against the caller's devices before.
/// </summary>
internal static class Detached
{
    public static Task Run(Func<Task> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work, CancellationToken.None);
        }
    }

    public static Task<T> Run<T>(Func<Task<T>> work)
    {
        using (ExecutionContext.SuppressFlow())
        {
            return Task.Run(work, CancellationToken.None);
        }
    }
}
