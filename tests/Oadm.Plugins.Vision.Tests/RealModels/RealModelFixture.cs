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
/// The real RF-DETR model uploaded once through the real <see cref="ModelLibrary"/> into a temp data folder, and the real
/// <see cref="OnnxInference"/> over it on the CPU only: no graphics card is looked for and a session on anything but the
/// CPU fails the test.
/// </summary>
public sealed class RealModelFixture : IDisposable
{
    /// <summary>The largest chunk the library takes, as the page sends them.</summary>
    public const int Chunk = VisionLimits.UploadChunkBytes;

    private readonly TempFolder _data = new();
    private readonly SemaphoreSlim _once = new(1, 1);
    private ModelLibrary? _library;
    private OnnxInference? _inference;
    private ModelCheck? _check;

    public ModelLibrary Library => _library ?? throw new InvalidOperationException("Not uploaded yet.");

    public OnnxInference Inference => _inference ?? throw new InvalidOperationException("Not uploaded yet.");

    /// <summary>Uploads the model on the first call; every later call returns the same check.</summary>
    public async Task<ModelCheck> UploadAsync()
    {
        await _once.WaitAsync();
        try
        {
            if (_check is not null)
            {
                return _check;
            }

            var path = RealModelFiles.PathOf(RealModelFiles.RfDetrMedium);
            _library = new ModelLibrary(new MemoryPluginRecords(), _data.Path, TimeProvider.System);
            await _library.LoadAsync(CancellationToken.None);
            var size = new FileInfo(path).Length;
            var id = await _library.BeginUploadAsync(RealModelFiles.RfDetrMedium, size, null, CancellationToken.None);
            var buffer = new byte[Chunk];
            await using (var file = File.OpenRead(path))
            {
                long offset = 0;
                int read;
                while ((read = await file.ReadAsync(buffer)) > 0)
                {
                    await _library.AppendAsync(id, offset, buffer.AsMemory(0, read), CancellationToken.None);
                    offset += read;
                }
            }

            _check = await _library.FinishUploadAsync(id, CancellationToken.None);
            var library = _library;
            _inference = new OnnxInference(new InferenceOptions
            {
                FileOf = library.FileOf,
                FindGraphicsCard = _ => throw new InvalidOperationException("The RealModel tests run on the CPU only."),
                CreateSession = static (file, target) => target == ExecutionTarget.Cpu
                    ? SessionFactory.Create(file, target, threads: 1)
                    : throw new InvalidOperationException("The RealModel tests run on the CPU only."),
            });
            return _check;
        }
        finally
        {
            _once.Release();
        }
    }

    /// <summary>The uploaded model (the check must have passed).</summary>
    public async Task<VisionModel> ModelAsync() =>
        (await UploadAsync()).Model ?? throw new InvalidOperationException("The model check refused the model: " + _check?.Refusal);

    public void Dispose()
    {
        _inference?.Dispose();
        _library?.Dispose();
        _data.Dispose();
        _once.Dispose();
    }
}
