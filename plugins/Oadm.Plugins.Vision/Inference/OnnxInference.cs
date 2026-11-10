using System.Buffers;
using System.Diagnostics;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;

namespace Oadm.Plugins.Vision.Inference;

/// <summary>How the inference starts.</summary>
public sealed record InferenceOptions
{
    /// <summary>The file of a stored model by its id; null when it is Missing (<see cref="IModelLibrary.FileOf"/>).</summary>
    public required Func<string, string?> FileOf { get; init; }

    /// <summary>The processor count the workers are computed from (<see cref="VisionLimits.Workers"/>).</summary>
    public int ProcessorCount { get; init; } = Environment.ProcessorCount;

    /// <summary>The clock of the load, the idle sessions and the card's limits (tests: a fake one).</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The graphics card provider to try (tests: never a real card; the CPU stands in for one).</summary>
    internal Func<ILogger, ExecutionTarget?> FindGraphicsCard { get; init; } = OnnxInference.FindGraphicsCard;

    /// <summary>Makes a session of a model file on a target (tests: one that fails on the card).</summary>
    internal Func<string, ExecutionTarget, InferenceSession> CreateSession { get; init; } = static (path, target) => SessionFactory.Create(path, target, threads: 1);

    /// <summary>The graphics card's name (tests: a fixed one, no provider is asked).</summary>
    internal Func<ExecutionTarget, string> CardName { get; init; } = SessionFactory.GraphicsCardName;

    /// <summary>Whether a model runs on the card by its test's times (tests: a fixed answer, no timing decides).</summary>
    internal Func<double?, double, bool> CardWorth { get; init; } = EngineRules.Worth;
}

/// <summary>
/// Runs the stored models with ONNX Runtime (docs/spec/vision-center.md "Load", "Models"): every model call on the CPU takes
/// one of <see cref="VisionLimits.Workers"/> workers and runs on one thread; all callers share one session per model, input
/// size and target, freed after <see cref="IdleTimeout"/> unused (never while a call holds it). With "Use GPU" on and a
/// graphics card (DirectML on Windows x64, CUDA on Linux x64) each model is tested there at its first call: two calls on the
/// card (the first warms it) against one warmed call on the CPU; a model clearly slower on the card
/// (<see cref="EngineRules.Worth"/>) stays on the CPU, its callers wait for the test. Card calls run one at a time; a card
/// call that fails or hangs ends the card's use with one log line and the call runs on the CPU. With Use GPU off no provider
/// is loaded.
/// </summary>
public sealed class OnnxInference : IInference, IDisposable
{
    /// <summary>A session unused for this long is freed.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);

    /// <summary>A model's test on the card takes at most this long; then it stays on the CPU.</summary>
    public static readonly TimeSpan CardTestLimit = TimeSpan.FromMinutes(1);

    /// <summary>A call on the card that takes longer ends the card's use (a hung driver).</summary>
    public static readonly TimeSpan CardCallLimit = TimeSpan.FromMinutes(2);

    private readonly InferenceOptions _options;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _lane = new(1, 1);
    private readonly BusyMeter _cpuBusy;
    private readonly BusyMeter _cardBusy;
    private readonly CombinedBusy _busy;
    private readonly Lock _lock = new();
    private readonly Dictionary<SessionKey, Held> _sessions = [];
    private readonly Dictionary<ProbeKey, Task<CardProbe>> _probes = [];
    private readonly ITimer _trim;
    private readonly string _cpuText;
    private Task? _cardTry;
    private CardState? _card;
    private int _waiting;
    private int _testing;
    private volatile bool _useGpu;
    private bool _disposed;

    public OnnxInference(InferenceOptions options, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        Capacity = VisionLimits.Workers(options.ProcessorCount);
        _slots = new SemaphoreSlim(Capacity, Capacity);
        _cpuBusy = new BusyMeter(options.Time);
        _cardBusy = new BusyMeter(options.Time);
        _busy = new CombinedBusy(_cpuBusy, _cardBusy, Capacity);
        _cpuText = EngineRules.CpuText(options.ProcessorCount);
        var period = IdleTimeout / 2;
        _trim = options.Time.CreateTimer(_ => TrimIdle(), null, period, period);
        InferenceLog.RunsOn(_logger, RunsOn);
    }

    /// <inheritdoc />
    public TimeSpan BusyTime => _busy.Busy;

    /// <inheritdoc />
    public int Capacity { get; }

    /// <inheritdoc />
    public bool HasRoom => _slots.CurrentCount > 0 && Volatile.Read(ref _waiting) == 0 && (!CardInUse || _lane.CurrentCount > 0);

    /// <inheritdoc />
    public string RunsOn => OnGpu && _card?.Name is { } name ? EngineRules.CardText(_options.ProcessorCount, name) : _cpuText;

    /// <inheritdoc />
    public bool OnGpu
    {
        get
        {
            if (!CardInUse)
            {
                return false;
            }

            lock (_lock)
            {
                return _probes.Values.Any(p => p.IsCompletedSuccessfully && p.Result.OnCard);
            }
        }
    }

    /// <inheritdoc />
    public bool GpuAvailable => _card is { Failed: false };

    /// <inheritdoc />
    public bool Starting => _cardTry is { IsCompleted: false } || Volatile.Read(ref _testing) > 0;

    /// <summary>Model calls on the CPU now (tests: a gate holds them).</summary>
    internal int BusyWorkers => Capacity - _slots.CurrentCount;

    /// <summary>Sessions kept now (tests: idle trimming, Forget).</summary>
    internal int Sessions
    {
        get
        {
            lock (_lock)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>Tests: runs inside each CPU call before the model (a gate that holds the worker).</summary>
    internal Func<Task>? BeforeCpuCall { get; set; }

    /// <summary>Tests: runs before each card call of a run (one that throws stands for a failing card).</summary>
    internal Action? BeforeCardCall { get; set; }

    /// <summary>Tests: runs in each card test before its calls (one that blocks stands for a hanging card).</summary>
    internal Action? BeforeCardTest { get; set; }

    private int _cardWaiting;

    /// <summary>Tests: the card calls waiting for the card now.</summary>
    internal int CardWaiting => Volatile.Read(ref _cardWaiting);

    private bool CardInUse => _useGpu && _card is { Failed: false };

    /// <summary>The card's time of one call of <paramref name="modelId"/> in ms once it was tested there; null before or on the CPU.</summary>
    public double? GpuMsOf(string modelId)
    {
        lock (_lock)
        {
            return _probes.Where(p => p.Key.ModelId == modelId && p.Value.IsCompletedSuccessfully).Select(p => p.Value.Result.CardMs).FirstOrDefault(ms => ms is not null);
        }
    }

    /// <inheritdoc />
    public IVisionFrame Decode(ReadOnlyMemory<byte> jpeg) => VisionFrame.Decode(jpeg);

    /// <inheritdoc />
    public IVisionFrame Decode(ReadOnlyMemory<byte> jpeg, PictureNeed? need) => VisionFrame.Decode(jpeg, need);

    /// <summary>Read from the picture's header only: an 8K frame is never decoded just to be held.</summary>
    public bool Fits(ReadOnlyMemory<byte> jpeg) => PictureLimits.Fits(jpeg);

    /// <inheritdoc />
    public async Task<IReadOnlyList<Detection>> DetectAsync(VisionModel model, IVisionFrame frame, double minScore, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        var picture = PictureOf(frame);
        if (model.Kind != ModelKind.Detection)
        {
            throw new ArgumentException("Not a detection model.", nameof(model));
        }

        var min = (float)Math.Clamp(minScore, 0, 1);
        return await RunAsync(
            model,
            tensor => Preprocess.Write(picture.Levels, null, model.Fit, model.Scaling, model.InputWidth, model.InputHeight, tensor),
            (outputs, fit) => ToDetections(model, Decoders.Detect(model.Format, outputs, min, model.InputWidth, model.InputHeight), fit),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClassResult>> ClassifyAsync(VisionModel model, IVisionFrame frame, IReadOnlyList<RelativeBox> regions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(regions);
        var picture = PictureOf(frame);
        if (model.Kind != ModelKind.Classification)
        {
            throw new ArgumentException("Not a classification model.", nameof(model));
        }

        // Each region a call of its own: the regions of one picture spread over the free workers.
        var calls = new Task<ClassResult>[regions.Count];
        for (var i = 0; i < calls.Length; i++)
        {
            var region = picture.PixelsOf(regions[i]);
            calls[i] = RunAsync(
                model,
                tensor => Preprocess.Write(picture.Levels, region, model.Fit, model.Scaling, model.InputWidth, model.InputHeight, tensor),
                (outputs, _) =>
                {
                    var (id, score) = Decoders.Classify(outputs);
                    return new ClassResult(id, NameOf(model, id), score);
                },
                ct);
        }

        return await Task.WhenAll(calls).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SetUseGpuAsync(bool useGpu)
    {
        var changed = _useGpu != useGpu;
        _useGpu = useGpu;
        if (changed && (!useGpu || _card is not null))
        {
            InferenceLog.RunsOn(_logger, RunsOn);
        }

        if (!useGpu)
        {
            return Task.CompletedTask;
        }

        lock (_lock)
        {
            if (_card is not null || _disposed)
            {
                return Task.CompletedTask;
            }

            return _cardTry is { IsCompleted: false } running ? running : _cardTry = Task.Run(FindCard);
        }
    }

    /// <inheritdoc />
    public void Forget(string modelId)
    {
        List<InferenceSession> free = [];
        lock (_lock)
        {
            foreach (var key in _sessions.Keys.Where(k => k.ModelId == modelId).ToList())
            {
                var held = _sessions[key];
                _sessions.Remove(key);
                held.Forgotten = true;
                if (held.InUse == 0)
                {
                    free.Add(held.Session);
                }
            }

            foreach (var key in _probes.Keys.Where(k => k.ModelId == modelId).ToList())
            {
                _probes.Remove(key);
            }
        }

        foreach (var session in free)
        {
            session.Dispose();
        }
    }

    /// <summary>The provider of this platform, or null with one log line; no session is made here.</summary>
    internal static ExecutionTarget? FindGraphicsCard(ILogger logger)
    {
        var target = SessionFactory.GraphicsCardTarget();
        if (target is null)
        {
            InferenceLog.NoGraphicsCardProvider(logger);
        }

        return target;
    }

    /// <summary>Looks for the card's provider; never faults (a failure: the CPU, one log line).</summary>
    private void FindCard()
    {
        ExecutionTarget? found;
        try
        {
            found = _options.FindGraphicsCard(_logger);
        }
#pragma warning disable CA1031 // Whatever the provider check throws, the CPU works.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            InferenceLog.GraphicsCardFailed(_logger, ex.Message);
            return;
        }

        if (found is not { } target)
        {
            return;
        }

        lock (_lock)
        {
            if (!_disposed)
            {
                _card = new CardState(target);
            }
        }
    }

    private static VisionFrame PictureOf(IVisionFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame as VisionFrame ?? throw new ArgumentException("Not a frame of this inference.", nameof(frame));
    }

    private static string NameOf(VisionModel model, int id) =>
        id >= 0 && id < model.ClassNames.Count ? model.ClassNames[id] : ModelTexts.ClassName(id);

    private static List<Detection> ToDetections(VisionModel model, List<RawDetection> found, FitTransform fit)
    {
        var result = new List<Detection>(found.Count);
        foreach (var d in found)
        {
            var name = NameOf(model, d.ClassId);
            if (name == VisionLimits.NoClassName || fit.ToPicture(d.X1, d.Y1, d.X2, d.Y2) is not { } box)
            {
                continue;
            }

            result.Add(new Detection(d.ClassId, name, Math.Round(d.Score, 4), box));
        }

        return result;
    }

    /// <summary>One model call: the picture into a rented input, the model on the card or a CPU worker, its outputs read in place.</summary>
    private async Task<T> RunAsync<T>(VisionModel model, Func<float[], FitTransform> prepare, Func<IModelOutputs, FitTransform, T> read, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = _options.FileOf(model.Id) ?? throw new InvalidOperationException(ModelTexts.UploadAgain(model.Name));
        var size = 3 * model.InputWidth * model.InputHeight;
        var tensor = ArrayPool<float>.Shared.Rent(size);
        try
        {
            if (await OnCardAsync(model, path, ct).ConfigureAwait(false) is { } card)
            {
                var fit = await Task.Run(() => prepare(tensor), ct).ConfigureAwait(false);
                var (outcome, value) = await TryCardCallAsync(card, model, path, tensor, fit, read, ct).ConfigureAwait(false);
                if (outcome == CardOutcome.Ran)
                {
                    return value!;
                }

                if (outcome == CardOutcome.Hung)
                {
                    // The hung call may still read the input: it never goes back to the pool, the CPU gets a new one.
                    tensor = ArrayPool<float>.Shared.Rent(size);
                }
            }

            return await CpuCallAsync(model, path, held => Call(held, model, tensor, prepare(tensor), read), ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(tensor);
        }
    }

    /// <summary>Runs <paramref name="work"/> on a free CPU worker with the model's shared CPU session.</summary>
    private async Task<T> CpuCallAsync<T>(VisionModel model, string path, Func<Held, T> work, CancellationToken ct)
    {
        Interlocked.Increment(ref _waiting);
        try
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _waiting);
        }

        try
        {
            if (BeforeCpuCall is { } gate)
            {
                await gate().ConfigureAwait(false);
            }

            var held = Take(new SessionKey(model.Id, model.InputWidth, model.InputHeight, ExecutionTarget.Cpu), path);
            var started = _cpuBusy.Start();
            try
            {
                // Off the caller's thread: the call blocks its thread for its length.
                return await Task.Run(() => work(held), ct).ConfigureAwait(false);
            }
            finally
            {
                _cpuBusy.Stop(started);
                Return(held);
            }
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>The call on the card; not run (the CPU next) when the card fails or hangs, which ends its use.</summary>
    private async Task<(CardOutcome Outcome, T? Value)> TryCardCallAsync<T>(CardState card, VisionModel model, string path, float[] tensor, FitTransform fit, Func<IModelOutputs, FitTransform, T> read, CancellationToken ct)
    {
        // A card that fails while this call waits ends the wait: a hung card test may hold the lane for good.
        var lane = _lane.WaitAsync(ct);
        Interlocked.Increment(ref _cardWaiting);
        try
        {
            if (await Task.WhenAny(lane, card.WhenFailed).ConfigureAwait(false) != lane)
            {
                _ = lane.ContinueWith(static (t, l) => ((SemaphoreSlim)l!).Release(), _lane, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return (CardOutcome.NotRun, default);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _cardWaiting);
        }

        await lane.ConfigureAwait(false);
        Held? held = null;
        var hung = false;
        try
        {
            if (card.Failed)
            {
                return (CardOutcome.NotRun, default);
            }

            held = Take(new SessionKey(model.Id, model.InputWidth, model.InputHeight, card.Target), path);
            var started = _cardBusy.Start();
            var call = Task.Run(
                () =>
                {
                    BeforeCardCall?.Invoke();
                    return Call(held, model, tensor, fit, read);
                },
                CancellationToken.None);
            try
            {
                // Not cut short by the token: the call holds its session and the input until it ends.
                return (CardOutcome.Ran, await call.WaitAsync(CardCallLimit, _options.Time, CancellationToken.None).ConfigureAwait(false));
            }
            catch (TimeoutException)
            {
                // The call still holds its session and the input: neither is freed under it.
                hung = true;
                Fail(card, "a call did not end within 2 minutes");
                return (CardOutcome.Hung, default);
            }
            finally
            {
                _cardBusy.Stop(started);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // A card that fails during a run leaves the models to the CPU (docs/spec/ocr.md "GPU").
        catch (Exception ex) when (ex is not ObjectDisposedException)
#pragma warning restore CA1031
        {
            Fail(card, ex.Message);
            return (CardOutcome.NotRun, default);
        }
        finally
        {
            if (held is not null && !hung)
            {
                Return(held);
            }

            _lane.Release();
        }
    }

    private void Fail(CardState card, string reason)
    {
        if (card.MarkFailed())
        {
            InferenceLog.GraphicsCardFailed(_logger, reason);
            InferenceLog.RunsOn(_logger, RunsOn);
        }
    }

    /// <summary>The card for this model when Use GPU is on, the card works and the model passed its test there (waits for the test).</summary>
    private async Task<CardState?> OnCardAsync(VisionModel model, string path, CancellationToken ct)
    {
        if (!_useGpu || _card is not { Failed: false } card)
        {
            return null;
        }

        Task<CardProbe> probe;
        var key = new ProbeKey(model.Id, model.InputWidth, model.InputHeight);
        lock (_lock)
        {
            if (!_probes.TryGetValue(key, out probe!))
            {
                Interlocked.Increment(ref _testing);
                probe = _probes[key] = ProbeAsync(card, model, path);
            }
        }

        var result = await probe.WaitAsync(ct).ConfigureAwait(false);
        return result.OnCard && !card.Failed ? card : null;
    }

    /// <summary>
    /// The card's test of one model at one input size: its session on the card, two blank calls there (the first warms it,
    /// the second counts) and a warmed blank call on the CPU. At most <see cref="CardTestLimit"/>; never faults.
    /// </summary>
    private async Task<CardProbe> ProbeAsync(CardState card, VisionModel model, string path)
    {
        try
        {
            var size = 3 * model.InputWidth * model.InputHeight;
            var blank = new float[size];
            var fit = FitTransform.Of(ModelFit.Stretch, model.InputWidth, model.InputHeight, model.InputWidth, model.InputHeight);
            double? cardMs = null;
            using var limit = new CancellationTokenSource(CardTestLimit, _options.Time);
            var test = Task.Run(async () =>
            {
                await _lane.WaitAsync(limit.Token).ConfigureAwait(false);
                try
                {
                    BeforeCardTest?.Invoke();
                    var held = Take(new SessionKey(model.Id, model.InputWidth, model.InputHeight, card.Target), path);
                    try
                    {
                        Call(held, model, blank, fit, static (_, _) => 0);
                        var watch = Stopwatch.StartNew();
                        Call(held, model, blank, fit, static (_, _) => 0);
                        return watch.Elapsed.TotalMilliseconds;
                    }
                    finally
                    {
                        Return(held);
                    }
                }
                finally
                {
                    _lane.Release();
                }
            });

            try
            {
                cardMs = await test.WaitAsync(CardTestLimit, _options.Time).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && limit.IsCancellationRequested))
            {
                // A card whose test hangs is not used again until the next start (its lane may stay held).
                Fail(card, "the test of " + model.Name + " did not end within 1 minute");
                return new CardProbe(false, null);
            }
#pragma warning disable CA1031 // A model the card cannot run stays on the CPU, one log line.
            catch (Exception ex) when (ex is not ObjectDisposedException)
#pragma warning restore CA1031
            {
                InferenceLog.CardModelFailed(_logger, model.Name, ex.Message);
                return new CardProbe(false, null);
            }

            var cpuMs = await CpuCallAsync(model, path, held =>
            {
                Call(held, model, blank, fit, static (_, _) => 0);
                var watch = Stopwatch.StartNew();
                Call(held, model, blank, fit, static (_, _) => 0);
                return watch.Elapsed.TotalMilliseconds;
            }, CancellationToken.None).ConfigureAwait(false);

            var onCard = _options.CardWorth(cardMs, cpuMs);
            var (card1, cpu1) = (Math.Round(cardMs!.Value, 1), Math.Round(cpuMs, 1));
            if (onCard)
            {
                card.Name ??= _options.CardName(card.Target);
                InferenceLog.CardModelUsed(_logger, model.Name, card1, cpu1);
            }
            else
            {
                InferenceLog.CardModelSlower(_logger, model.Name, card1, cpu1);
            }

            return new CardProbe(onCard, card1);
        }
#pragma warning disable CA1031 // Never faults: the callers go on on the CPU.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            InferenceLog.CardModelFailed(_logger, model.Name, ex.Message);
            return new CardProbe(false, null);
        }
        finally
        {
            Interlocked.Decrement(ref _testing);
        }
    }

    /// <summary>Runs the model of <paramref name="held"/> on <paramref name="tensor"/> and reads its outputs in place.</summary>
    private static T Call<T>(Held held, VisionModel model, float[] tensor, FitTransform fit, Func<IModelOutputs, FitTransform, T> read)
    {
        using var inputs = new ModelInputs(held.Session, held.Input, tensor, model.InputWidth, model.InputHeight);
        using var options = new RunOptions(); // each call its own: one session runs many calls at once
        using var results = held.Session.Run(options, inputs.Names, inputs.Values, held.OutputNames(model.Format));
        return read(new OrtOutputs(held.OutputNames(model.Format), results), fit);
    }

    private Held Take(SessionKey key, string path)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sessions.TryGetValue(key, out var held))
            {
                held.InUse++;
                return held;
            }
        }

        // Made outside the lock (a second or more for a large model); a second caller of the same key may make one too,
        // the first one stored wins and the other is freed.
        var session = _options.CreateSession(path, key.Target);
        var input = PictureInput.Of(session);
        if (input is null)
        {
            session.Dispose();
            throw new InvalidOperationException(ModelTexts.CannotRun(ModelTexts.NoPictureInput));
        }

        lock (_lock)
        {
            if (_disposed)
            {
                session.Dispose();
                throw new ObjectDisposedException(GetType().Name);
            }

            if (_sessions.TryGetValue(key, out var other))
            {
                session.Dispose();
                other.InUse++;
                return other;
            }

            var made = new Held(session, input) { InUse = 1 };
            _sessions[key] = made;
            return made;
        }
    }

    private void Return(Held held)
    {
        bool free;
        lock (_lock)
        {
            held.InUse--;
            held.Since = _options.Time.GetTimestamp();
            free = (held.Forgotten || _disposed) && held.InUse == 0;
        }

        if (free)
        {
            held.Session.Dispose();
        }
    }

    /// <summary>Frees the sessions unused for <see cref="IdleTimeout"/>.</summary>
    internal void TrimIdle()
    {
        var now = _options.Time.GetTimestamp();
        List<InferenceSession> free = [];
        lock (_lock)
        {
            foreach (var (key, held) in _sessions.ToList())
            {
                if (held.InUse == 0 && _options.Time.GetElapsedTime(held.Since, now) >= IdleTimeout)
                {
                    _sessions.Remove(key);
                    free.Add(held.Session);
                }
            }
        }

        foreach (var session in free)
        {
            session.Dispose();
        }
    }

    public void Dispose()
    {
        List<InferenceSession> free = [];
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var held in _sessions.Values)
            {
                if (held.InUse == 0)
                {
                    free.Add(held.Session);
                }
            }

            _sessions.Clear();
        }

        _trim.Dispose();
        foreach (var session in free)
        {
            session.Dispose();
        }
    }

    private enum CardOutcome
    {
        Ran,
        NotRun,
        Hung,
    }

    private readonly record struct SessionKey(string ModelId, int Width, int Height, ExecutionTarget Target);

    private readonly record struct ProbeKey(string ModelId, int Width, int Height);

    private sealed record CardProbe(bool OnCard, double? CardMs);

    /// <summary>The graphics card in use: its provider, its name once a model ran there, and whether it failed.</summary>
    private sealed class CardState(ExecutionTarget target)
    {
        private readonly TaskCompletionSource _failedSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _failed;

        public ExecutionTarget Target { get; } = target;

        /// <summary>Ends when the card failed.</summary>
        public Task WhenFailed => _failedSource.Task;

        public string? Name { get; set; }

        public bool Failed => Volatile.Read(ref _failed) != 0;

        /// <summary>The first failure ends the card's use; true for the caller that tells the log.</summary>
        public bool MarkFailed()
        {
            var first = Interlocked.Exchange(ref _failed, 1) == 0;
            _failedSource.TrySetResult();
            return first;
        }
    }

    /// <summary>A shared session with the calls that hold it now.</summary>
    private sealed class Held(InferenceSession session, PictureInput input)
    {
        private string[]? _all;
        private string[]? _boxesAndLogits;

        public InferenceSession Session { get; } = session;

        public PictureInput Input { get; } = input;

        public int InUse { get; set; }

        public long Since { get; set; }

        public bool Forgotten { get; set; }

        /// <summary>The outputs a format reads: all, except the masks of RF-DETR's segmentation models.</summary>
        public string[] OutputNames(ModelFormat format) => format == ModelFormat.RfDetr && Session.OutputNames.Count > 2
            ? _boxesAndLogits ??= [.. Session.OutputNames.Take(2)]
            : _all ??= [.. Session.OutputNames];
    }

    /// <summary>The outputs of one call where ONNX Runtime left them (valid only during the call).</summary>
    private sealed class OrtOutputs(string[] names, IDisposableReadOnlyCollection<OrtValue> values) : IModelOutputs
    {
        private readonly long[]?[] _shapes = new long[]?[names.Length];
        private readonly float[]?[] _converted = new float[]?[names.Length];

        public int Count => names.Length;

        public string Name(int index) => names[index];

        public long[] Shape(int index) => _shapes[index] ??= values[index].GetTensorTypeAndShape().Shape;

        public bool IsFloat(int index) => HalfPrecision.IsFloat(values[index].GetTensorTypeAndShape().ElementDataType);

        public ReadOnlySpan<float> Floats(int index) =>
            IsFloat(index) ? HalfPrecision.Floats(values[index], ref _converted[index]) : throw new InvalidOperationException(ModelTexts.OutputsDoNotFit);

        public double ValueAt(int index, int at) => values[index].GetTensorTypeAndShape().ElementDataType switch
        {
            TensorElementType.Float or TensorElementType.Float16 => Floats(index)[at],
            TensorElementType.Int64 => values[index].GetTensorDataAsSpan<long>()[at],
            TensorElementType.Int32 => values[index].GetTensorDataAsSpan<int>()[at],
            TensorElementType.Double => values[index].GetTensorDataAsSpan<double>()[at],
            _ => throw new InvalidOperationException("Not a numeric output."),
        };
    }
}
