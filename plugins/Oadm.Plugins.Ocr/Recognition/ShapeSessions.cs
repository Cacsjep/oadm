using Microsoft.ML.OnnxRuntime;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>
/// The sessions of one model. On the CPU and with CUDA one session takes every input size. DirectML runs a session fast only
/// for the input size it saw first (measured: a small area read after a full HD search took 160 instead of 38 ms), so there
/// each input size gets a session of its own, kept in the card's <see cref="GpuSessionBudget"/> (counted by its input pixels,
/// at most <see cref="GpuSessionBudget.MaxBytes"/> together, freed after 2 idle minutes). The callers keep the sizes few
/// (sides in steps, the tiles of an area in calls of one size).
/// </summary>
internal sealed class ShapeSessions : IDisposable
{
    private readonly string _path;
    private readonly ExecutionTarget _target;
    private readonly int _threads;
    private readonly GpuSessionBudget? _budget;
    private readonly InferenceSession? _single;
    private readonly RunOptions _runOptions = new();
    private InferenceSession? _fresh;

    /// <param name="budget">DirectML: the sessions on the card (required there); else unused.</param>
    public ShapeSessions(string path, ExecutionTarget target, int threads, GpuSessionBudget? budget = null)
    {
        _path = path;
        _target = target;
        _threads = threads;
        if (target != ExecutionTarget.DirectMl)
        {
            _single = SessionFactory.Create(path, target, threads);
            InputName = _single.InputNames[0];
        }
        else
        {
            _budget = budget ?? throw new ArgumentNullException(nameof(budget));

            // One session for the name, kept for the first size (it has not seen one yet).
            _fresh = SessionFactory.Create(path, target, threads);
            InputName = _fresh.InputNames[0];
        }
    }

    /// <summary>The model's input.</summary>
    public string InputName { get; }

    /// <summary>Reads a model's first output in place (valid only during the call).</summary>
    public delegate T OutputReader<out T>(ReadOnlySpan<float> output, ReadOnlySpan<long> shape);

    /// <summary>
    /// Runs the model on the first values of <paramref name="input"/> (shape <paramref name="shape"/>) and hands its first output
    /// to <paramref name="read"/> where ONNX Runtime left it: the input is pinned, not copied, the output never copied whole.
    /// </summary>
    public T Run<T>(float[] input, long[] shape, OutputReader<T> read)
    {
        if (_single is not null)
        {
            return RunOn(_single, input, shape, read);
        }

        // DirectML: the session of this size, held in the budget's gate for the call (never freed while it runs).
        lock (_budget!.Gate)
        {
            // Counted by the input's pixels: [count, channels, height, width].
            var bytes = GpuSessionBudget.Estimate(shape[0] * shape[2] * shape[3]);
            var session = _budget.Get(this, string.Join('x', shape), bytes, () => Interlocked.Exchange(ref _fresh, null) ?? SessionFactory.Create(_path, _target, _threads));
            return RunOn(session, input, shape, read);
        }
    }

    private T RunOn<T>(InferenceSession session, float[] input, long[] shape, OutputReader<T> read)
    {
        var length = 1L;
        foreach (var d in shape)
        {
            length *= d;
        }

        using var value = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, input.AsMemory(0, (int)length), shape);
        using var results = session.Run(_runOptions, [InputName], [value], [session.OutputNames[0]]);
        var output = results[0];
        return read(output.GetTensorDataAsSpan<float>(), output.GetTensorTypeAndShape().Shape);
    }

    public void Dispose()
    {
        _single?.Dispose();
        _budget?.Release(this);
        Interlocked.Exchange(ref _fresh, null)?.Dispose();
        _runOptions.Dispose();
    }
}
