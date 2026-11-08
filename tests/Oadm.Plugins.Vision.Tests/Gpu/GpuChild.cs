using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;

using Oadm.Core.Plugins;
using Oadm.Plugins.Vision.Inference;
using Oadm.Plugins.Vision.Models;
using Oadm.Plugins.Vision.Tests.Models;
using Oadm.Plugins.Vision.Tests.RealModels;
using Oadm.Sdk.Plugins;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.Vision.Tests.Gpu;

/// <summary>
/// The entry point of this test assembly as a fresh process of its own (<see cref="GpuSupport.RunChildAsync"/>): the Gpu
/// tests need a process in which nothing loaded ONNX Runtime or DirectML before. Without arguments it does nothing.
/// </summary>
public static class GpuChild
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "cpu-only" => await CpuOnlyChild.RunAsync(),
                "both" => await BothAddOnsChild.RunAsync(args[1], int.Parse(args[2], CultureInfo.InvariantCulture)),
                _ => 2,
            };
        }
#pragma warning disable CA1031 // The parent reads the failure from the output.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Console.WriteLine("FAILED " + ex);
            return 1;
        }
    }

    internal static void Result(object result) => Console.WriteLine("RESULT " + JsonSerializer.Serialize(result));
}

/// <summary>Vision Center with Use GPU off in a fresh process: a model check and a detection, then the loaded libraries.</summary>
internal static class CpuOnlyChild
{
    public static async Task<int> RunAsync()
    {
        using var data = new TempFolder();
        using var library = new ModelLibrary(new MemoryPluginRecords(), data.Path, TimeProvider.System);
        await library.LoadAsync(CancellationToken.None);
        var model = (await Upload.TypedAsync(library, RealModelFiles.Yolo11n)).Model!;
        using var inference = new OnnxInference(new InferenceOptions { FileOf = library.FileOf }, new ConsoleLog("vision"));
        await inference.SetUseGpuAsync(false);
        using var frame = inference.Decode(await File.ReadAllBytesAsync(RealModelFiles.PathOf(RealModelFiles.Skaters)));
        var found = await inference.DetectAsync(model, frame, 0.25, CancellationToken.None);
        await inference.SetUseGpuAsync(false);
        GpuChild.Result(new
        {
            onGpu = inference.OnGpu,
            gpuAvailable = inference.GpuAvailable,
            runsOn = inference.RunsOn,
            found = found.Count,
            modules = GpuSupport.NativeModules(),
        });
        return 0;
    }
}

/// <summary>Uploads a real model file in chunks as the page does.</summary>
internal static class Upload
{
    public static async Task<Oadm.Plugins.Vision.Model.ModelCheck> TypedAsync(ModelLibrary library, string file, string? labels = null)
    {
        var path = RealModelFiles.PathOf(file);
        var id = await library.BeginUploadAsync(file, new FileInfo(path).Length, labels, CancellationToken.None);
        var buffer = new byte[RealModelFixture.Chunk];
        await using var stream = File.OpenRead(path);
        long offset = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            await library.AppendAsync(id, offset, buffer.AsMemory(0, read), CancellationToken.None);
            offset += read;
        }

        return await library.FinishUploadAsync(id, CancellationToken.None);
    }

    /// <summary>The same through reflection, for a library of a plugin load context; the model or the refusal as an exception.</summary>
    public static async Task<object> ReflectedAsync(object library, string file)
    {
        var path = RealModelFiles.PathOf(file);
        var id = (string)(await Remote.CallAsync(library, "BeginUploadAsync", file, new FileInfo(path).Length, null, CancellationToken.None))!;
        var buffer = new byte[RealModelFixture.Chunk];
        await using (var stream = File.OpenRead(path))
        {
            long offset = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                await Remote.CallAsync(library, "AppendAsync", id, offset, new ReadOnlyMemory<byte>(buffer, 0, read), CancellationToken.None);
                offset += read;
            }
        }

        var check = (await Remote.CallAsync(library, "FinishUploadAsync", id, CancellationToken.None))!;
        return Remote.Get(check, "Model") ?? throw new InvalidOperationException($"{file} refused: {Remote.Get(check, "Refusal")}");
    }
}

/// <summary>Calls into the types of a plugin load context (not the test's own types) by name.</summary>
internal static class Remote
{
    public static object New(Assembly assembly, string type, params object?[] args) =>
        Activator.CreateInstance(assembly.GetType(type, throwOnError: true)!, args)!;

    public static object? Call(object target, string name, params object?[] args)
    {
        var method = target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == name && m.GetParameters().Length == args.Length);
        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }

    public static async Task<object?> CallAsync(object target, string name, params object?[] args)
    {
        var task = (Task)Call(target, name, args)!;
        await task;
        return task.GetType().IsGenericType ? task.GetType().GetProperty("Result")!.GetValue(task) : null;
    }

    public static object? Get(object target, string name) => target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.GetValue(target);

    public static void Set(object target, string name, object? value) => target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)!.SetValue(target, value);
}

/// <summary>
/// Text recognition and Vision Center loaded as the server loads them (<see cref="PluginLoader.LoadPackage"/>: each folder in
/// a <see cref="PluginLoadContext"/> of its own, native libraries per context), both with Use GPU on, started in
/// <c>order</c> ("ocr-first", "vision-first", "together") and then reading and detecting at the same time for
/// <c>seconds</c>. Results against each add-on's own CPU run in the same context.
/// </summary>
internal static partial class BothAddOnsChild
{
    public static async Task<int> RunAsync(string order, int seconds)
    {
        var memory = new Dictionary<string, int?> { ["before"] = GpuSupport.CardMemoryMb() };
        var registry = new PluginRegistry();
        var loader = new PluginLoader(registry);
        var ocrPackage = loader.LoadPackage(Path.Combine(GpuPlugins.Root, GpuPlugins.Ocr));
        var visionPackage = loader.LoadPackage(Path.Combine(GpuPlugins.Root, GpuPlugins.Vision));
        if (ocrPackage is null || visionPackage is null)
        {
            throw new InvalidOperationException("Not loaded: " + string.Join("; ", registry.Errors.Select(e => e.Message)));
        }

        Console.WriteLine($"Loaded {ocrPackage.Manifest.Id} ({ocrPackage.LoadContext.Name}) and {visionPackage.Manifest.Id} ({visionPackage.LoadContext.Name}); native libraries now: {GpuSupport.NativeModules().Count}");
        await using var ocr = new OcrSide(ocrPackage.Assemblies.Single(a => a.GetName().Name == "Oadm.Plugins.Ocr.Server"), ocrPackage.Directory);
        using var vision = new VisionSide(visionPackage.Assemblies.Single(a => a.GetName().Name == "Oadm.Plugins.Vision.Server"));

        switch (order)
        {
            case "ocr-first":
                await ocr.StartAsync();
                await ocr.AloneAsync(10);
                await vision.StartAsync();
                break;
            case "vision-first":
                await vision.StartAsync();
                await vision.AloneAsync(10);
                await ocr.StartAsync();
                break;
            case "together":
                await Task.WhenAll(Task.Run(ocr.StartAsync), Task.Run(vision.StartAsync));
                break;
            default:
                throw new ArgumentException("Unknown order " + order, nameof(order));
        }

        memory["started"] = GpuSupport.CardMemoryMb();
        Console.WriteLine($"Both started: OCR on {ocr.RunsOn} (on GPU {ocr.OnGpu}), Vision on {vision.RunsOn} (on GPU {vision.OnGpu})");

        var watch = Stopwatch.StartNew();
        var until = TimeSpan.FromSeconds(seconds);
        var peak = memory["started"];
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (GpuSupport.CardMemoryMb() is { } mb && (peak is null || mb > peak))
                {
                    peak = mb;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stop.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });
        await Task.WhenAll(Task.Run(() => ocr.LoopAsync(watch, until)), Task.Run(() => vision.LoopAsync(watch, until)));
        await stop.CancelAsync();
        await sampler;
        memory["peak"] = peak;

        var result = new
        {
            order,
            seconds,
            ocr = ocr.Report(),
            vision = vision.Report(),
            modules = GpuSupport.NativeModules(),
            memoryMb = memory,
        };
        await ocr.DisposeAsync();
        vision.Dispose();
        memory["end"] = GpuSupport.CardMemoryMb();
        GpuChild.Result(result);
        return 0;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    internal static string Normalize(string text) => Spaces().Replace(text.Trim(), " ");

    /// <summary>The median in ms, rounded; null without a call.</summary>
    internal static double? Ms(List<double> times) => times.Count == 0 ? null : Math.Round(GpuSupport.Median(times), 1);

    /// <summary>Text recognition in its own context: one recognizer on the CPU for the reference, one with Use GPU on.</summary>
    private sealed class OcrSide(Assembly assembly, string folder) : IAsyncDisposable
    {
        private const string Picture = "box-1920x1080.jpg";
        private readonly List<double> _alone = [];
        private readonly List<double> _both = [];
        private readonly List<string> _errors = [];
        private readonly List<string> _mismatches = [];
        private object? _cpu;
        private object? _card;
        private object? _areas;
        private byte[] _jpeg = [];
        private string[] _expected = [];
        private string[] _reference = [];
        private string[] _first = [];
        private bool _disposed;

        public bool OnGpu => _card is not null && (bool)Remote.Get(_card, "OnGpu")!;

        public string RunsOn => _card is null ? "-" : (string)Remote.Get(Remote.Get(_card, "RunsOn")!, "Name")!;

        public async Task StartAsync()
        {
            // The camera box of the accuracy set: four fixed-place areas of any text.
            var set = TestRepository.Path("tests", "Oadm.Plugins.Ocr.Tests", "Fixtures", "AccuracySet");
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(set, "set.json")));
            var picture = json.RootElement.GetProperty("pictures").EnumerateArray()
                .First(p => p.GetProperty("file").GetString() == Picture && p.GetProperty("group").GetString() == "camera-box");
            _jpeg = await File.ReadAllBytesAsync(Path.Combine(set, Picture));
            var areaType = assembly.GetType("Oadm.Plugins.Ocr.Model.Area", throwOnError: true)!;
            var boxType = areaType.GetProperty("Box")!.PropertyType;
            var areas = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(areaType))!;
            var expected = new List<string>();
            foreach (var a in picture.GetProperty("areas").EnumerateArray())
            {
                var b = a.GetProperty("box").EnumerateArray().Select(v => v.GetDouble()).ToArray();
                var area = Activator.CreateInstance(areaType)!;
                Remote.Set(area, "Name", a.GetProperty("name").GetString());
                Remote.Set(area, "Box", Activator.CreateInstance(boxType, b[0], b[1], b[2], b[3]));
                areas.Add(area);
                expected.Add(a.GetProperty("expected").GetString()!);
            }

            _areas = areas;
            _expected = [.. expected];
            _cpu = Remote.New(assembly, "Oadm.Plugins.Ocr.Recognition.OnnxTextRecognizer", Options(false), new ConsoleLog("ocr cpu"));
            _reference = await ReadAsync(_cpu);
            Console.WriteLine("OCR on the CPU: " + string.Join(" | ", _reference));
            var watch = Stopwatch.StartNew();
            _card = Remote.New(assembly, "Oadm.Plugins.Ocr.Recognition.OnnxTextRecognizer", Options(true), new ConsoleLog("ocr"));
            await (Task)Remote.Call(_card, "SetUseGpu", true)!;
            Console.WriteLine($"OCR: the card's start took {watch.ElapsedMilliseconds} ms; on GPU {OnGpu}, runs on {RunsOn}");
            _first = await ReadAsync(_card);
            Console.WriteLine("OCR with Use GPU on: " + string.Join(" | ", _first));
        }

        private object Options(bool gpu)
        {
            var options = Activator.CreateInstance(assembly.GetType("Oadm.Plugins.Ocr.Recognition.OcrEngineOptions", throwOnError: true)!)!;
            Remote.Set(options, "ModelDirectory", Path.Combine(folder, "Models"));
            Remote.Set(options, "UseGraphicsCard", gpu);
            return options;
        }

        private async Task<string[]> ReadAsync(object recognizer)
        {
            var picture = (await Remote.CallAsync(recognizer, "ReadAsync", new ReadOnlyMemory<byte>(_jpeg), _areas, CancellationToken.None))!;
            return [.. ((IEnumerable)Remote.Get(picture, "LinesPerArea")!).Cast<IEnumerable>()
                .Select(lines => Normalize(string.Join(" ", lines.Cast<object>().Select(l => (string)Remote.Get(l, "Text")!))))];
        }

        public async Task AloneAsync(int calls)
        {
            for (var i = 0; i < calls; i++)
            {
                await TimedAsync(_alone);
            }
        }

        public async Task LoopAsync(Stopwatch watch, TimeSpan until)
        {
            while (watch.Elapsed < until)
            {
                await TimedAsync(_both);
            }
        }

        private async Task TimedAsync(List<double> times)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var read = await ReadAsync(_card!);
                times.Add(watch.Elapsed.TotalMilliseconds);
                if (!read.SequenceEqual(_reference) && _mismatches.Count < 10)
                {
                    _mismatches.Add(string.Join(" | ", read));
                }
            }
#pragma warning disable CA1031 // Reported, not thrown: the other add-on goes on.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (_errors.Count < 10)
                {
                    _errors.Add(ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        public object Report() => new
        {
            onGpu = OnGpu,
            gpuAvailable = _card is not null && (bool)Remote.Get(_card, "GpuAvailable")!,
            runsOn = RunsOn,
            expected = _expected,
            cpu = _reference,
            card = _first,
            aloneCalls = _alone.Count,
            aloneMs = Ms(_alone),
            bothCalls = _both.Count,
            bothMs = Ms(_both),
            mismatches = _mismatches,
            errors = _errors,
        };

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var recognizer in new[] { _card, _cpu })
            {
                if (recognizer is IAsyncDisposable disposable)
                {
                    await disposable.DisposeAsync();
                }
            }
        }
    }

    /// <summary>Vision Center in its own context: YOLO11n and RF-DETR medium, one inference on the CPU for the reference, one with Use GPU on.</summary>
    private sealed class VisionSide(Assembly assembly) : IDisposable
    {
        private static readonly (string File, double MinScore)[] Files = [(RealModelFiles.Yolo11nNoNames, 0.25), (RealModelFiles.RfDetrMedium, 0.5)];
        private readonly TempFolder _data = new();
        private readonly List<double> _alone = [];
        private readonly Dictionary<string, List<double>> _both = [];
        private readonly List<string> _errors = [];
        private readonly List<string> _mismatches = [];
        private readonly List<(object Model, string Name, double MinScore, List<Found> Reference)> _models = [];
        private readonly Dictionary<string, object> _comparisons = [];
        private readonly List<string> _log = [];
        private object? _library;
        private object? _cpu;
        private object? _card;
        private object? _frame;
        private bool _disposed;

        public bool OnGpu => _card is not null && (bool)Remote.Get(_card, "OnGpu")!;

        public string RunsOn => _card is null ? "-" : (string)Remote.Get(_card, "RunsOn")!;

        public async Task StartAsync()
        {
            _library = Remote.New(assembly, "Oadm.Plugins.Vision.Models.ModelLibrary", new MemoryPluginRecords(), _data.Path, TimeProvider.System, new ConsoleLog("vision"));
            await Remote.CallAsync(_library, "LoadAsync", CancellationToken.None);
            var library = _library;
            _cpu = Remote.New(assembly, "Oadm.Plugins.Vision.Inference.OnnxInference", Options(library), new ConsoleLog("vision cpu"));
            _frame = Remote.Call(_cpu, "Decode", new ReadOnlyMemory<byte>(await File.ReadAllBytesAsync(RealModelFiles.PathOf(RealModelFiles.Skaters))))!;
            foreach (var (file, minScore) in Files)
            {
                var model = await Upload.ReflectedAsync(library, file);
                var reference = await DetectAsync(_cpu, model, minScore);
                Console.WriteLine($"Vision {file} on the CPU: {string.Join("; ", reference)}");
                _models.Add((model, file, minScore, reference));
                _both[file] = [];
            }

            var watch = Stopwatch.StartNew();
            _card = Remote.New(assembly, "Oadm.Plugins.Vision.Inference.OnnxInference", Options(library), new ConsoleLog("vision", _log));
            await (Task)Remote.Call(_card, "SetUseGpuAsync", true)!;
            foreach (var (model, name, minScore, reference) in _models)
            {
                // The first call tests the model on the card.
                var found = await DetectAsync(_card, model, minScore);
                var comparison = GpuSupport.Compare(reference, found, minScore);
                var stem = Path.GetFileNameWithoutExtension(name);
                bool onCard;
                lock (_log)
                {
                    onCard = _log.Exists(l => l.Contains("the model " + stem + " runs on the graphics card", StringComparison.Ordinal));
                }

                _comparisons[name] = new { OnCard = onCard, comparison.Same, comparison.Pairs, MinIoU = Math.Round(comparison.MinIoU, 4), MaxScoreDiff = Math.Round(comparison.MaxScoreDiff, 4), comparison.Diff, cardTestMs = Remote.Call(_card, "GpuMsOf", Remote.Get(model, "Id")) };
                Console.WriteLine($"Vision {name} with Use GPU on: {string.Join("; ", found)}");
            }

            Console.WriteLine($"Vision: the card's start and tests took {watch.ElapsedMilliseconds} ms; on GPU {OnGpu}, runs on {RunsOn}");
        }

        private object Options(object library)
        {
            var options = Activator.CreateInstance(assembly.GetType("Oadm.Plugins.Vision.Inference.InferenceOptions", throwOnError: true)!)!;
            Remote.Set(options, "FileOf", new Func<string, string?>(id => (string?)Remote.Call(library, "FileOf", id)));
            return options;
        }

        private async Task<List<Found>> DetectAsync(object inference, object model, double minScore)
        {
            var found = (IEnumerable)(await Remote.CallAsync(inference, "DetectAsync", model, _frame, minScore, CancellationToken.None))!;
            return [.. found.Cast<object>().Select(d =>
            {
                var box = Remote.Get(d, "Box")!;
                return new Found((string)Remote.Get(d, "ClassName")!, (double)Remote.Get(d, "Score")!, (double)Remote.Get(box, "X")!, (double)Remote.Get(box, "Y")!, (double)Remote.Get(box, "Width")!, (double)Remote.Get(box, "Height")!);
            })];
        }

        public async Task AloneAsync(int calls)
        {
            for (var i = 0; i < calls; i++)
            {
                await TimedAsync(_models[0], _alone);
            }
        }

        public async Task LoopAsync(Stopwatch watch, TimeSpan until)
        {
            for (var i = 0; watch.Elapsed < until; i++)
            {
                var entry = _models[i % _models.Count];
                await TimedAsync(entry, _both[entry.Name]);
            }
        }

        private async Task TimedAsync((object Model, string Name, double MinScore, List<Found> Reference) entry, List<double> times)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var found = await DetectAsync(_card!, entry.Model, entry.MinScore);
                times.Add(watch.Elapsed.TotalMilliseconds);
                if (GpuSupport.Compare(entry.Reference, found, entry.MinScore) is { Same: false } diff && _mismatches.Count < 10)
                {
                    _mismatches.Add(entry.Name + ": " + diff.Diff);
                }
            }
#pragma warning disable CA1031 // Reported, not thrown: the other add-on goes on.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                if (_errors.Count < 10)
                {
                    _errors.Add(ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        public object Report() => new
        {
            onGpu = OnGpu,
            gpuAvailable = _card is not null && (bool)Remote.Get(_card, "GpuAvailable")!,
            runsOn = RunsOn,
            models = _comparisons,
            reference = _models.ToDictionary(m => m.Name, m => m.Reference.Select(f => f.ToString()).ToList()),
            aloneCalls = _alone.Count,
            aloneMs = Ms(_alone),
            bothCalls = _both.ToDictionary(b => b.Key, b => b.Value.Count),
            bothMs = _both.ToDictionary(b => b.Key, b => Ms(b.Value)),
            mismatches = _mismatches,
            errors = _errors,
        };

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            (_frame as IDisposable)?.Dispose();
            (_card as IDisposable)?.Dispose();
            (_cpu as IDisposable)?.Dispose();
            (_library as IDisposable)?.Dispose();
            _data.Dispose();
        }
    }
}
