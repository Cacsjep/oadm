using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;

using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.LoadTest.Checking;

/// <summary>A kept run: its summary and the device scope that ran it (docs/spec/device-scope.md, Shared objects).</summary>
internal sealed record StoredRun(RunSummary Summary, string Owner);

/// <summary>The graphs and rows of a run (detail file).</summary>
internal sealed record RunDetailData(RunGraphs Graphs, IReadOnlyList<StreamRow> Streams);

/// <summary>The per-second values of one stream over its batch.</summary>
internal sealed record StreamSeries(float[] Mbps, float[] Loss, float[] Jitter);

/// <summary>The per-second values of every stream of one batch, with the phase of each second.</summary>
internal sealed record BatchSeries(IReadOnlyList<CheckPhase> Phases, IReadOnlyDictionary<string, StreamSeries> Streams);

/// <summary>
/// The kept runs (at most <see cref="LoadTestInfo.MaxRuns"/>): the summaries as records of the plugin
/// (<c>ICorePluginContext.Records</c>, collection "runs", one per run), the graphs and rows of a run and the per-second
/// values of each batch as gzipped JSON files in the plugin's data folder (<c>runs/&lt;run id&gt;/detail.json.gz</c>,
/// <c>runs/&lt;run id&gt;/batch-&lt;n&gt;.json.gz</c>). Hosts without records or data folder keep them in memory.
/// </summary>
internal sealed class RunStore
{
    private const string Collection = "runs";
    private readonly IPluginRecords _records;
    private readonly string? _folder;
    private readonly ConcurrentDictionary<string, byte[]> _memory = new(StringComparer.Ordinal);

    public RunStore(IPluginRecords? records, string? dataDirectory)
    {
        _records = records ?? new MemoryPluginRecords();
        _folder = dataDirectory is null ? null : Path.Combine(dataDirectory, "runs");
    }

    public async Task<IReadOnlyList<StoredRun>> LoadAsync(CancellationToken ct)
    {
        var list = await _records.ListAsync(Collection, ct).ConfigureAwait(false);
        var runs = new List<StoredRun>();
        foreach (var json in list.Values)
        {
            try
            {
                runs.Add(LoadTestJson.Deserialize<StoredRun>(json));
            }
            catch (JsonException)
            {
                // A record OADM cannot read is left out (it is deleted with the next retention).
            }
        }

        return [.. runs.OrderByDescending(r => r.Summary.StartedUtc)];
    }

    public Task SaveSummaryAsync(StoredRun run, CancellationToken ct) =>
        _records.SetManyAsync(Collection, [KeyValuePair.Create<string, string?>(Key(run.Summary.RunId), LoadTestJson.Serialize(run))], ct);

    public Task SaveDetailAsync(Guid runId, RunDetailData detail, CancellationToken ct) => WriteAsync(runId, "detail", detail, ct);

    public Task SaveBatchAsync(Guid runId, int batch, BatchSeries series, CancellationToken ct) => WriteAsync(runId, BatchName(batch), series, ct);

    public Task<RunDetailData?> ReadDetailAsync(Guid runId, CancellationToken ct) => ReadAsync<RunDetailData>(runId, "detail", ct);

    public Task<BatchSeries?> ReadBatchAsync(Guid runId, int batch, CancellationToken ct) => ReadAsync<BatchSeries>(runId, BatchName(batch), ct);

    public async Task DeleteAsync(Guid runId, CancellationToken ct)
    {
        await _records.SetManyAsync(Collection, [KeyValuePair.Create<string, string?>(Key(runId), null)], ct).ConfigureAwait(false);
        if (_folder is null)
        {
            foreach (var key in _memory.Keys.Where(k => k.StartsWith(Key(runId) + "/", StringComparison.Ordinal)).ToList())
            {
                _memory.TryRemove(key, out _);
            }

            return;
        }

        var folder = Path.Combine(_folder, Key(runId));
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static string Key(Guid runId) => runId.ToString("N");

    private static string BatchName(int batch) => "batch-" + batch.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task WriteAsync<T>(Guid runId, string name, T value, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            await JsonSerializer.SerializeAsync(gzip, value, LoadTestJson.Options, ct).ConfigureAwait(false);
        }

        if (_folder is null)
        {
            _memory[Key(runId) + "/" + name] = buffer.ToArray();
            return;
        }

        var folder = Path.Combine(_folder, Key(runId));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".json.gz");
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, buffer.ToArray(), ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private async Task<T?> ReadAsync<T>(Guid runId, string name, CancellationToken ct)
        where T : class
    {
        byte[]? bytes;
        if (_folder is null)
        {
            bytes = _memory.TryGetValue(Key(runId) + "/" + name, out var kept) ? kept : null;
        }
        else
        {
            var path = Path.Combine(_folder, Key(runId), name + ".json.gz");
            bytes = File.Exists(path) ? await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false) : null;
        }

        if (bytes is null)
        {
            return null;
        }

        try
        {
            using var gzip = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            return await JsonSerializer.DeserializeAsync<T>(gzip, LoadTestJson.Options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return null;
        }
    }
}
