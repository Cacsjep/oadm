using System.Collections.Concurrent;

using Oadm.Plugins.Vision.Inference;
using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Models;
using Oadm.Plugins.Vision.Tests.Models;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Vision.Tests.RealModels;

[CollectionDefinition(Name)]
public sealed class RealModelGroup : ICollectionFixture<RealModelFixture>
{
    public const string Name = "RealModel";
}

/// <summary>
/// Each real model uploaded once through the real <see cref="ModelLibrary"/> (a library of its own per model in a temp data
/// folder, so the library's model limit never decides), and one real <see cref="OnnxInference"/> over all of them on the CPU
/// only: no graphics card is looked for and a session on anything but the CPU fails the test.
/// </summary>
public sealed class RealModelFixture : IDisposable
{
    /// <summary>The largest chunk the library takes, as the page sends them.</summary>
    public const int Chunk = VisionLimits.UploadChunkBytes;

    private readonly TempFolder _data = new();
    private readonly SemaphoreSlim _once = new(1, 1);
    private readonly Dictionary<string, (ModelLibrary Library, ModelCheck Check)> _uploads = [];
    private readonly ConcurrentDictionary<string, ModelLibrary> _libraryOf = new();
    private readonly OnnxInference _inference;

    public RealModelFixture()
    {
        _inference = new OnnxInference(new InferenceOptions
        {
            FileOf = id => _libraryOf.TryGetValue(id, out var library) ? library.FileOf(id) : null,
            FindGraphicsCard = _ => throw new InvalidOperationException("The RealModel tests run on the CPU only."),
            CreateSession = static (file, target) => target == ExecutionTarget.Cpu
                ? SessionFactory.Create(file, target, threads: 1)
                : throw new InvalidOperationException("The RealModel tests run on the CPU only."),
        });
    }

    public OnnxInference Inference => _inference;

    /// <summary>The library <paramref name="file"/> was uploaded to (after <see cref="UploadAsync"/>).</summary>
    public ModelLibrary LibraryOf(string file) => _uploads.TryGetValue(file, out var upload) ? upload.Library : throw new InvalidOperationException("Not uploaded yet.");

    /// <summary>
    /// Uploads the model file <paramref name="file"/> (with the labels file <paramref name="labels"/>) in chunks on the first
    /// call; every later call returns the same check.
    /// </summary>
    public async Task<ModelCheck> UploadAsync(string file, string? labels = null)
    {
        await _once.WaitAsync();
        try
        {
            if (_uploads.TryGetValue(file, out var done))
            {
                return done.Check;
            }

            var path = RealModelFiles.PathOf(file);
            var library = new ModelLibrary(new MemoryPluginRecords(), Path.Combine(_data.Path, _uploads.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), TimeProvider.System);
            await library.LoadAsync(CancellationToken.None);
            var size = new FileInfo(path).Length;
            var id = await library.BeginUploadAsync(file, size, labels, CancellationToken.None);
            var buffer = new byte[Chunk];
            await using (var stream = File.OpenRead(path))
            {
                long offset = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    await library.AppendAsync(id, offset, buffer.AsMemory(0, read), CancellationToken.None);
                    offset += read;
                }
            }

            var check = await library.FinishUploadAsync(id, CancellationToken.None);
            if (check.Model is { } model)
            {
                _libraryOf[model.Id] = library;
            }

            _uploads[file] = (library, check);
            return check;
        }
        finally
        {
            _once.Release();
        }
    }

    /// <summary>The uploaded model of <paramref name="file"/> (the check must have passed).</summary>
    public async Task<VisionModel> ModelAsync(string file, string? labels = null)
    {
        var check = await UploadAsync(file, labels);
        return check.Model ?? throw new InvalidOperationException("The model check refused the model: " + check.Refusal);
    }

    public void Dispose()
    {
        _inference.Dispose();
        foreach (var (library, _) in _uploads.Values)
        {
            library.Dispose();
        }

        _data.Dispose();
        _once.Dispose();
    }
}
