using Microsoft.ML.OnnxRuntime;

namespace Oadm.Plugins.Ocr.Recognition;

/// <summary>
/// The sessions of one model. On the CPU and with CUDA one session takes every input size. DirectML runs a session fast only
/// for the input size it saw first (measured: a small area read after a full HD search took 160 instead of 38 ms), so there
/// each input size gets a session of its own, at most <see cref="MaxDirectMlSessions"/> (the least recently used one goes).
/// The callers keep the sizes few (sizes in steps, lines padded to fixed batches).
/// </summary>
internal sealed class ShapeSessions : IDisposable
{
    /// <summary>The input sizes kept at once per model on DirectML.</summary>
    public const int MaxDirectMlSessions = 12;

    private readonly string _path;
    private readonly ExecutionTarget _target;
    private readonly int _threads;
    private readonly object _lock = new();
    private readonly Dictionary<string, (InferenceSession Session, long Used)> _bySize = [];
    private readonly InferenceSession? _single;
    private readonly RunOptions _runOptions = new();
    private long _clock;

    public ShapeSessions(string path, ExecutionTarget target, int threads)
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
            // One session for the name, kept as the first size's session.
            var first = SessionFactory.Create(path, target, threads);
            InputName = first.InputNames[0];
            _bySize[string.Empty] = (first, 0);
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
        var length = 1L;
        foreach (var d in shape)
        {
            length *= d;
        }

        var session = SessionFor(shape);
        using var value = OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, input.AsMemory(0, (int)length), shape);
        using var results = session.Run(_runOptions, [InputName], [value], [session.OutputNames[0]]);
        var output = results[0];
        return read(output.GetTensorDataAsSpan<float>(), output.GetTensorTypeAndShape().Shape);
    }

    private InferenceSession SessionFor(long[] dimensions)
    {
        if (_single is not null)
        {
            return _single;
        }

        var key = string.Join('x', dimensions);
        lock (_lock)
        {
            _clock++;
            if (_bySize.TryGetValue(key, out var found))
            {
                _bySize[key] = (found.Session, _clock);
                return found.Session;
            }

            // The session made at the start has not seen a size yet: it takes the first one.
            if (_bySize.Remove(string.Empty, out var fresh))
            {
                _bySize[key] = (fresh.Session, _clock);
                return fresh.Session;
            }

            if (_bySize.Count >= MaxDirectMlSessions)
            {
                var oldest = _bySize.MinBy(e => e.Value.Used);
                _bySize.Remove(oldest.Key);
                oldest.Value.Session.Dispose();
            }

            var session = SessionFactory.Create(_path, _target, _threads);
            _bySize[key] = (session, _clock);
            return session;
        }
    }

    public void Dispose()
    {
        _single?.Dispose();
        _runOptions.Dispose();
        lock (_lock)
        {
            foreach (var (session, _) in _bySize.Values)
            {
                session.Dispose();
            }

            _bySize.Clear();
        }
    }
}
