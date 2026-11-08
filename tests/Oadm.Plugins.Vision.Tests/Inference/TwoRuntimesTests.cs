using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

using Oadm.Core.Plugins;
using Oadm.Plugins.Vision.Tests.Models;

using Xunit.Abstractions;

namespace Oadm.Plugins.Vision.Tests.Inference;

/// <summary>
/// The open point of docs/spec/vision-center.md: Text recognition and Vision Center each bring ONNX Runtime and run at the
/// same time in one server. Each add-on is a collectible <see cref="PluginLoadContext"/> of its own folder; this loads
/// <c>Microsoft.ML.OnnxRuntime.dll</c> with its native library from two separate folders into two such contexts (the native
/// library resolved per context by <see cref="PluginLoadContext"/>'s <c>LoadUnmanagedDll</c>), runs a small model in both
/// at once (and in the test's own copy beside them), on the CPU only: the native library of the CPU build or of the DirectML
/// build, never a graphics card provider.
/// </summary>
public sealed class TwoRuntimesTests : IDisposable
{
    /// <summary>The runtimes' copies: a loaded native library stays until the process ends, so each run deletes the older runs' copies.</summary>
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "oadm-vision-runtimes");

    private readonly ITestOutputHelper _output;
    private readonly string _folder;

    public TwoRuntimesTests(ITestOutputHelper output)
    {
        _output = output;
        if (Directory.Exists(Root))
        {
            foreach (var old in Directory.GetDirectories(Root))
            {
                TryDelete(old);
            }
        }

        _folder = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => TryDelete(_folder); // the copies loaded now stay until the test process ends

    private static void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string NativeName =>
        OperatingSystem.IsWindows() ? "onnxruntime.dll" : OperatingSystem.IsMacOS() ? "libonnxruntime.dylib" : "libonnxruntime.so";

    /// <summary>A copy of the add-on's runtime in a folder of its own: the managed and the native library side by side, as in a plugin folder.</summary>
    private string RuntimeFolder(string name)
    {
        var folder = Path.Combine(_folder, name);
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Microsoft.ML.OnnxRuntime.dll"), Path.Combine(folder, "Microsoft.ML.OnnxRuntime.dll"));
        var native = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native"), NativeName).FirstOrDefault()
            ?? Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "runtimes"), NativeName, SearchOption.AllDirectories)
                .First(p => p.Contains(RuntimeInformation.OSArchitecture.ToString(), StringComparison.OrdinalIgnoreCase));
        File.Copy(native, Path.Combine(folder, NativeName));
        return folder;
    }

    /// <summary>y = x * 2 for x of 4 floats: a real operator on the input, so the native library computes.</summary>
    private static byte[] TimesTwo()
    {
        static Proto Value(string name) => new Proto().String(1, name).Message(2, new Proto().Message(1, new Proto().Varint(1, OnnxModel.Float).Message(2, new Proto().Message(1, OnnxModel.Dim(4)))));
        var two = new Proto().Varint(1, 1).Varint(2, OnnxModel.Float).String(8, "two").Bytes(9, BitConverter.GetBytes(2f));
        var mul = new Proto().String(1, "x").String(1, "two").String(2, "y").String(4, "Mul");
        var graph = new Proto().Message(5, two).Message(1, mul).String(2, "times_two").Message(11, Value("x")).Message(12, Value("y"));
        return new Proto().Varint(1, 8).Message(7, graph).Message(8, new Proto().String(1, string.Empty).Varint(2, 17)).ToArray();
    }

    [Fact]
    public async Task Two_add_ons_run_their_own_onnx_runtime_at_the_same_time()
    {
        var model = TimesTwo();
        var ocr = new PluginLoadContext(Path.Combine(RuntimeFolder("oadm.ocr"), "Microsoft.ML.OnnxRuntime.dll"), "oadm.ocr");
        var vision = new PluginLoadContext(Path.Combine(RuntimeFolder("oadm.vision"), "Microsoft.ML.OnnxRuntime.dll"), "oadm.vision");
        try
        {
            using var first = new Runtime(ocr, model);
            using var second = new Runtime(vision, model);
            using var own = new Microsoft.ML.OnnxRuntime.InferenceSession(model); // the test's own copy, in the default context
            Assert.NotSame(first.Assembly, second.Assembly);
            Assert.NotSame(first.Assembly, typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly);

            // Both at once, many calls each, with the default context's copy running beside them.
            using var start = new SemaphoreSlim(0, 3);
            var watch = Stopwatch.StartNew();
            var runs = new[]
            {
                Task.Run(async () => { await start.WaitAsync(); return Repeat(i => first.Run([i, 1, 2, 3])); }),
                Task.Run(async () => { await start.WaitAsync(); return Repeat(i => second.Run([i, -1, 0.5f, 10])); }),
                Task.Run(async () => { await start.WaitAsync(); return Repeat(i => RunOwn(own, [i, 4, 5, 6])); }),
            };
            start.Release(3);
            var results = await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(60));

            Assert.All(results, Assert.True);
            Assert.Equal([10f, 2, 4, 6], first.Run([5, 1, 2, 3]));
            Assert.Equal([2f, -2, 1, 20], second.Run([1, -1, 0.5f, 10]));

            // Each context loaded the native library of its own folder (the process's module list: Windows and Linux).
            if (OperatingSystem.IsMacOS())
            {
                return;
            }

            var modules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Where(m => string.Equals(m.ModuleName, NativeName, StringComparison.OrdinalIgnoreCase)).Select(m => m.FileName).ToList();
            _output.WriteLine($"{watch.ElapsedMilliseconds} ms for 3 x {Calls} calls; native libraries loaded:");
            foreach (var module in modules)
            {
                _output.WriteLine("  " + module);
            }

            Assert.Contains(modules, m => m.Contains(Path.Combine(_folder, "oadm.ocr"), StringComparison.OrdinalIgnoreCase));
            Assert.Contains(modules, m => m.Contains(Path.Combine(_folder, "oadm.vision"), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            ocr.Unload();
            vision.Unload();
        }
    }

    private const int Calls = 200;

    private static bool Repeat(Func<float, float[]> run)
    {
        for (var i = 0; i < Calls; i++)
        {
            var y = run(i);
            if (y[0] != i * 2)
            {
                return false;
            }
        }

        return true;
    }

    private static float[] RunOwn(Microsoft.ML.OnnxRuntime.InferenceSession session, float[] x)
    {
        using var input = Microsoft.ML.OnnxRuntime.OrtValue.CreateTensorValueFromMemory(x, [4]);
        using var options = new Microsoft.ML.OnnxRuntime.RunOptions();
        using var results = session.Run(options, ["x"], [input], ["y"]);
        return results[0].GetTensorDataAsSpan<float>().ToArray();
    }

    /// <summary>ONNX Runtime of one load context, called through reflection (its types are not the test's).</summary>
    private sealed class Runtime : IDisposable
    {
        private readonly object _session;
        private readonly MethodInfo _run;
        private readonly MethodInfo _toTensor;
        private readonly MethodInfo _named;
        private readonly Type _list;

        public Runtime(PluginLoadContext context, byte[] model)
        {
            Assembly = context.LoadFromAssemblyName(new AssemblyName("Microsoft.ML.OnnxRuntime"));
            var session = Assembly.GetType("Microsoft.ML.OnnxRuntime.InferenceSession", throwOnError: true)!;
            _session = Activator.CreateInstance(session, [model])!;
            var value = Assembly.GetType("Microsoft.ML.OnnxRuntime.NamedOnnxValue", throwOnError: true)!;
            _list = typeof(List<>).MakeGenericType(value);
            _run = session.GetMethod("Run", [typeof(IReadOnlyCollection<>).MakeGenericType(value)])!;
            _toTensor = Assembly.GetType("Microsoft.ML.OnnxRuntime.Tensors.ArrayTensorExtensions", throwOnError: true)!
                .GetMethods().Single(m => m.Name == "ToTensor" && m.GetParameters()[0].ParameterType.IsSZArray).MakeGenericMethod(typeof(float));
            _named = value.GetMethods().Single(m => m.Name == "CreateFromTensor" && m.IsGenericMethod).MakeGenericMethod(typeof(float));
        }

        public Assembly Assembly { get; }

        public float[] Run(float[] x)
        {
            var inputs = (IList)Activator.CreateInstance(_list)!;
            inputs.Add(_named.Invoke(null, ["x", _toTensor.Invoke(null, [x])]));
            using var results = (IDisposable)_run.Invoke(_session, [inputs])!;
            var first = ((IEnumerable)results).Cast<object>().Single();
            var tensor = (IEnumerable)first.GetType().GetProperty("Value")!.GetValue(first)!;
            return [.. tensor.Cast<float>()];
        }

        public void Dispose() => ((IDisposable)_session).Dispose();
    }
}
