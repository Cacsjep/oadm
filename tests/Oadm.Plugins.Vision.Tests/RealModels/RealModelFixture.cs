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
/// The real models, each uploaded once through one real <see cref="ModelLibrary"/> into a temp data folder, and the real
/// <see cref="OnnxInference"/> over them on the CPU only: no graphics card is looked for and a session on anything but the
/// CPU fails the test.
/// </summary>
public sealed class RealModelFixture : IDisposable
{
    /// <summary>The largest chunk the library takes, as the page sends them.</summary>
    public const int Chunk = VisionLimits.UploadChunkBytes;

    private readonly TempFolder _data = new();
    private readonly SemaphoreSlim _once = new(1, 1);
    private readonly Dictionary<string, ModelCheck> _checks = [];
    private ModelLibrary? _library;
    private OnnxInference? _inference;

    public ModelLibrary Library => _library ?? throw new InvalidOperationException("Not uploaded yet.");

    public OnnxInference Inference => _inference ?? throw new InvalidOperationException("Not uploaded yet.");

    /// <summary>Uploads RF-DETR medium on the first call; every later call returns the same check.</summary>
    public Task<ModelCheck> UploadAsync() => UploadAsync(RealModelFiles.RfDetrMedium);

    /// <summary>
    /// Uploads the real model <paramref name="file"/> (with the labels file <paramref name="labels"/>) in chunks on the first
    /// call; every later call returns the same check.
    /// </summary>
    public async Task<ModelCheck> UploadAsync(string file, string? labels = null)
    {
        await _once.WaitAsync();
        try
        {
            if (_checks.TryGetValue(file, out var done))
            {
                return done;
            }

            if (_library is null)
            {
                _library = new ModelLibrary(new MemoryPluginRecords(), _data.Path, TimeProvider.System);
                await _library.LoadAsync(CancellationToken.None);
                var library = _library;
                _inference = new OnnxInference(new InferenceOptions
                {
                    FileOf = library.FileOf,
                    FindGraphicsCard = _ => throw new InvalidOperationException("The RealModel tests run on the CPU only."),
                    CreateSession = static (path, target) => target == ExecutionTarget.Cpu
                        ? SessionFactory.Create(path, target, threads: 1)
                        : throw new InvalidOperationException("The RealModel tests run on the CPU only."),
                });
            }

            var path = RealModelFiles.PathOf(file);
            var size = new FileInfo(path).Length;
            var id = await _library.BeginUploadAsync(file, size, labels, CancellationToken.None);
            var buffer = new byte[Chunk];
            await using (var stream = File.OpenRead(path))
            {
                long offset = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    await _library.AppendAsync(id, offset, buffer.AsMemory(0, read), CancellationToken.None);
                    offset += read;
                }
            }

            var check = await _library.FinishUploadAsync(id, CancellationToken.None);
            _checks[file] = check;
            return check;
        }
        finally
        {
            _once.Release();
        }
    }

    /// <summary>The uploaded RF-DETR medium (the check must have passed).</summary>
    public Task<VisionModel> ModelAsync() => ModelAsync(RealModelFiles.RfDetrMedium);

    /// <summary>The uploaded model <paramref name="file"/> (the check must have passed).</summary>
    public async Task<VisionModel> ModelAsync(string file, string? labels = null)
    {
        var check = await UploadAsync(file, labels);
        return check.Model ?? throw new InvalidOperationException($"The model check refused {file}: {check.Refusal}");
    }

    public void Dispose()
    {
        _inference?.Dispose();
        _library?.Dispose();
        _data.Dispose();
        _once.Dispose();
    }
}
