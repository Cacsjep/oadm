using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Vision.Models;

/// <summary>
/// The stored models (docs/spec/vision-center.md "Models"): one plugin record per model (collection <see cref="Collection"/>,
/// the list a backup keeps), the files in <c>models/&lt;id&gt;.onnx</c> of the plugin's data folder (never in a backup). An
/// upload comes in chunks into a temporary file there, hashed (SHA-256) on the way and never held in memory; a chunk must
/// come within <see cref="VisionLimits.UploadIdleTimeout"/> or the upload ends. The finished file passes the model check
/// (<see cref="ModelChecker"/>) or is deleted. A model whose file is gone (a restore on another computer) is
/// <see cref="VisionModel.Missing"/> until a file with the same SHA-256 is uploaded: it comes back with its settings. Writes
/// run one at a time; reads take the copy in memory.
/// </summary>
public sealed partial class ModelLibrary : IModelLibrary, IDisposable
{
    /// <summary>The records' collection.</summary>
    public const string Collection = "models";

    /// <summary>The records' collection of the user's class lists (a backup keeps them).</summary>
    public const string ListCollection = "classLists";

    /// <summary>The folder of the files in the plugin's data folder.</summary>
    public const string FolderName = "models";

    private const string PartSuffix = ".part";

    private readonly IPluginRecords _records;
    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<string, string?, CancellationToken, (CheckedModel? Model, string? Refusal)> _check;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly Lock _sync = new();
    private readonly Dictionary<string, Upload> _uploads = new(StringComparer.Ordinal);
    private List<VisionModel> _models = [];
    private List<ClassList> _lists = [];
    private bool _disposed;

    /// <param name="records">The plugin's records.</param>
    /// <param name="dataDirectory">The plugin's data folder; the files go into its <see cref="FolderName"/>.</param>
    /// <param name="time">The clock of the uploads' idle timeout and the upload times.</param>
    public ModelLibrary(IPluginRecords records, string dataDirectory, TimeProvider? time = null, ILogger? logger = null)
        : this(records, dataDirectory, time, logger, ModelChecker.Check)
    {
    }

    internal ModelLibrary(IPluginRecords records, string dataDirectory, TimeProvider? time, ILogger? logger, Func<string, string?, CancellationToken, (CheckedModel? Model, string? Refusal)> check)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _records = records;
        _folder = Path.Combine(dataDirectory, FolderName);
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _check = check;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public IReadOnlyList<VisionModel> All
    {
        get
        {
            lock (_sync)
            {
                return [.. _models];
            }
        }
    }

    /// <summary>The built-in class lists, then the user's by name.</summary>
    public IReadOnlyList<ClassList> ClassLists
    {
        get
        {
            lock (_sync)
            {
                return [.. Models.ClassLists.BuiltIn, .. _lists];
            }
        }
    }

    /// <summary>The class list of <paramref name="id"/> (built-in or the user's), or null.</summary>
    public ClassList? FindClassList(string? id)
    {
        if (Models.ClassLists.BuiltInOf(id) is { } builtIn)
        {
            return builtIn;
        }

        lock (_sync)
        {
            return _lists.Find(l => l.Id == id);
        }
    }

    /// <summary>The names of the models that use the class list <paramref name="id"/>, by name.</summary>
    public IReadOnlyList<string> ModelsUsing(string? id) =>
        [.. All.Where(m => m.ClassListId is not null && m.ClassListId == id).Select(m => m.Name).Order(StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Uploads in progress (tests: the idle timeout).</summary>
    internal int Uploads
    {
        get
        {
            lock (_sync)
            {
                return _uploads.Count;
            }
        }
    }

    /// <summary>The file of model <paramref name="id"/> in <paramref name="dataDirectory"/>.</summary>
    public static string PathOf(string dataDirectory, string id) => Path.Combine(dataDirectory, FolderName, id + ".onnx");

    /// <inheritdoc />
    public VisionModel? Find(string? id)
    {
        lock (_sync)
        {
            return _models.Find(m => m.Id == id);
        }
    }

    /// <inheritdoc />
    public string? FileOf(string id)
    {
        if (Find(id) is not { Missing: false } model)
        {
            return null;
        }

        var path = FilePath(model.Id);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Reads the stored models; a model whose file is not there (or not of its size) is Missing. Left-over upload files are deleted.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(_folder);
        foreach (var part in Directory.EnumerateFiles(_folder, "*" + PartSuffix))
        {
            TryDelete(part);
        }

        var stored = await _records.ListAsync(Collection, ct).ConfigureAwait(false);
        var models = new List<VisionModel>(stored.Count);
        foreach (var (key, json) in stored)
        {
            try
            {
                var model = VisionJson.Deserialize<VisionModel>(json) with { Id = key };
                var file = new FileInfo(FilePath(key));
                models.Add(model with { Missing = !file.Exists || file.Length != model.SizeBytes });
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                LogUnreadable(_logger, key, ex);
            }
        }

        var lists = new List<ClassList>();
        foreach (var (key, json) in await _records.ListAsync(ListCollection, ct).ConfigureAwait(false))
        {
            try
            {
                lists.Add(VisionJson.Deserialize<ClassList>(json) with { Id = key, BuiltIn = false });
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException)
            {
                LogUnreadable(_logger, key, ex);
            }
        }

        lock (_sync)
        {
            _lists = SortedLists(lists);
        }

        // A model on a list takes the list's names as they are now (a built-in list corrected by a newer OADM, a restored list).
        for (var i = 0; i < models.Count; i++)
        {
            if (FindClassList(models[i].ClassListId) is { } list && (models[i].ClassNames.Count == 0 || models[i].ClassNames.Count == list.Names.Count))
            {
                models[i] = models[i] with { ClassNames = list.Names };
            }
        }

        // A list that is gone (a user's list missing from a restore, the "coco-90" of an OADM that read RF-DETR as DETR): the
        // model keeps the names it holds as its own, with one log line; never a failed start.
        var fallen = new List<VisionModel>();
        for (var i = 0; i < models.Count; i++)
        {
            if (models[i].ClassListId is { } listId && FindClassList(listId) is null)
            {
                LogListGone(_logger, models[i].Name, listId);
                models[i] = models[i] with { ClassListId = null };
                fallen.Add(models[i]);
            }
        }

        if (fallen.Count > 0)
        {
            await _records.SetManyAsync(Collection, [.. fallen.Select(m => new KeyValuePair<string, string?>(m.Id, VisionJson.Serialize(m with { Id = string.Empty, Missing = false })))], ct).ConfigureAwait(false);
        }

        lock (_sync)
        {
            _models = Sorted(models);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Creates (empty id) or changes a user's class list; the models that use it take its names. Built-in lists cannot be
    /// changed; a list in use keeps the class count of its models.
    /// </summary>
    /// <exception cref="ClassListFieldException">A field is not allowed (the field and its text).</exception>
    /// <exception cref="ArgumentException">A built-in or unknown list, or too many lists.</exception>
    public async Task<ClassList> SaveClassListAsync(ClassList incoming, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (Models.ClassLists.BuiltInOf(incoming.Id) is not null)
        {
            throw new ArgumentException(ModelTexts.ClassListBuiltInChanged);
        }

        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var isNew = string.IsNullOrEmpty(incoming.Id);
            if (!isNew && FindClassList(incoming.Id) is null)
            {
                throw new ArgumentException(ModelTexts.ClassListGone);
            }

            var name = (incoming.Name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                throw new ClassListFieldException(ClassListFieldException.NameField, ModelTexts.NameMissing);
            }

            if (name.Length > VisionLimits.MaxNameLength)
            {
                throw new ClassListFieldException(ClassListFieldException.NameField, ModelTexts.NameTooLong);
            }

            if (ClassLists.FirstOrDefault(l => l.Id != incoming.Id && string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) is { } other)
            {
                throw new ClassListFieldException(ClassListFieldException.NameField, ModelTexts.ClassListNameTaken(other.Name));
            }

            var incomingNames = incoming.Names ?? [];
            if (incomingNames.Count == 0)
            {
                throw new ClassListFieldException(ClassListFieldException.NamesField, ModelTexts.ClassListNamesMissing);
            }

            if (incomingNames.Count > VisionLimits.MaxClasses)
            {
                throw new ClassListFieldException(ClassListFieldException.NamesField, ModelTexts.ClassListTooManyNames);
            }

            if (incomingNames.Any(n => (n ?? string.Empty).AsSpan().IndexOfAny('\r', '\n') >= 0))
            {
                throw new ClassListFieldException(ClassListFieldException.NamesField, ModelTexts.ClassNameLineBreak);
            }

            if (incomingNames.Any(n => (n ?? string.Empty).Trim().Length > VisionLimits.MaxNameLength))
            {
                throw new ClassListFieldException(ClassListFieldException.NamesField, ModelTexts.ClassNameTooLong);
            }

            IReadOnlyList<string> names = [.. incomingNames.Select((n, i) => ClassNames.Clean(n, i))];
            var users = isNew ? [] : All.Where(m => m.ClassListId == incoming.Id).ToList();
            if (users.FirstOrDefault(m => m.ClassNames.Count > 0 && m.ClassNames.Count != names.Count) is { } user)
            {
                throw new ClassListFieldException(ClassListFieldException.NamesField, ModelTexts.ClassListCountInUse(user.ClassNames.Count));
            }

            lock (_sync)
            {
                if (isNew && _lists.Count >= ModelLimits.MaxClassLists)
                {
                    throw new ArgumentException(ModelTexts.TooManyClassLists);
                }
            }

            var saved = new ClassList { Id = isNew ? Guid.NewGuid().ToString("N") : incoming.Id, Name = name, Names = names };
            await _records.SetManyAsync(ListCollection, [new(saved.Id, VisionJson.Serialize(saved with { Id = string.Empty }))], ct).ConfigureAwait(false);
            var followed = users.Select(m => m with { ClassNames = names }).ToList();
            if (followed.Count > 0)
            {
                await _records.SetManyAsync(Collection, [.. followed.Select(m => new KeyValuePair<string, string?>(m.Id, VisionJson.Serialize(m with { Id = string.Empty, Missing = false })))], ct).ConfigureAwait(false);
            }

            lock (_sync)
            {
                _lists = SortedLists([.. _lists.Where(l => l.Id != saved.Id), saved]);
                foreach (var model in followed)
                {
                    var index = _models.FindIndex(m => m.Id == model.Id);
                    if (index >= 0)
                    {
                        _models[index] = model with { Missing = _models[index].Missing };
                    }
                }
            }

            if (followed.Count > 0)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }

            return saved;
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Deletes a user's class list.</summary>
    /// <exception cref="InvalidOperationException">A built-in list, or models use it ("Used by 2 models: ...").</exception>
    /// <exception cref="ArgumentException">There is no such list.</exception>
    public async Task DeleteClassListAsync(string id, CancellationToken ct)
    {
        if (Models.ClassLists.BuiltInOf(id) is not null)
        {
            throw new InvalidOperationException(ModelTexts.ClassListBuiltIn);
        }

        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = FindClassList(id) ?? throw new ArgumentException(ModelTexts.ClassListGone);
            if (ModelsUsing(list.Id) is { Count: > 0 } users)
            {
                throw new InvalidOperationException(ModelTexts.UsedByModels(users));
            }

            await _records.SetManyAsync(ListCollection, [new(list.Id, null)], ct).ConfigureAwait(false);
            lock (_sync)
            {
                _lists.RemoveAll(l => l.Id == list.Id);
            }
        }
        finally
        {
            _write.Release();
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The file is not allowed (its kind, its size, the limits); the text says why.</exception>
    public Task<string> BeginUploadAsync(string fileName, long size, string? labels, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !fileName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(ModelTexts.NotOnnxFile);
        }

        if (size <= 0)
        {
            throw new ArgumentException(ModelTexts.Empty);
        }

        if (size > VisionLimits.MaxModelBytes)
        {
            throw new ArgumentException(ModelTexts.TooLarge);
        }

        if (labels is not null && ClassNames.FromLabels(labels) is null)
        {
            throw new ArgumentException(ModelTexts.TooManyLabels);
        }

        Directory.CreateDirectory(_folder);
        var id = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // Uploads in progress count; a Missing model's file may be the one that comes (checked again when it is stored).
            if (_models.Count(m => !m.Missing) + _uploads.Count >= VisionLimits.MaxModels)
            {
                throw new ArgumentException(ModelTexts.TooMany);
            }

            if (_models.Where(m => !m.Missing).Sum(m => m.SizeBytes) + _uploads.Values.Sum(u => u.Size) + size > VisionLimits.MaxAllModelsBytes)
            {
                throw new ArgumentException(ModelTexts.TooMuchTogether);
            }

            var part = Path.Combine(_folder, id + PartSuffix);
            var file = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            var upload = new Upload(id, Path.GetFileName(fileName), size, labels, file, part);
            upload.Idle = _time.CreateTimer(_ => _ = EndIdleAsync(id), null, VisionLimits.UploadIdleTimeout, Timeout.InfiniteTimeSpan);
            _uploads[id] = upload;
        }

        return Task.FromResult(id);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">The upload ended, a part is missing or the file grows past its size.</exception>
    public async Task AppendAsync(string uploadId, long offset, ReadOnlyMemory<byte> chunk, CancellationToken ct)
    {
        var upload = Running(uploadId);
        await upload.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (upload.Ended)
            {
                throw new ArgumentException(ModelTexts.UploadGone);
            }

            if (offset != upload.Written)
            {
                await EndAsync(upload).ConfigureAwait(false);
                throw new ArgumentException(ModelTexts.UploadOutOfOrder);
            }

            if (upload.Written + chunk.Length > upload.Size || chunk.Length > VisionLimits.UploadChunkBytes)
            {
                await EndAsync(upload).ConfigureAwait(false);
                throw new ArgumentException(ModelTexts.UploadTooLong);
            }

            upload.Idle?.Change(VisionLimits.UploadIdleTimeout, Timeout.InfiniteTimeSpan);
            upload.Hash.AppendData(chunk.Span);
            await upload.File.WriteAsync(chunk, ct).ConfigureAwait(false);
            upload.Written += chunk.Length;
        }
        finally
        {
            upload.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ModelCheck> FinishUploadAsync(string uploadId, CancellationToken ct)
    {
        var upload = Running(uploadId);
        await upload.Gate.WaitAsync(ct).ConfigureAwait(false);
        string part;
        string sha;
        try
        {
            if (upload.Ended)
            {
                return new ModelCheck(null, ModelTexts.UploadGone);
            }

            if (upload.Written != upload.Size)
            {
                await EndAsync(upload).ConfigureAwait(false);
                return new ModelCheck(null, ModelTexts.UploadTooShort);
            }

            upload.Idle?.Dispose();
            await upload.File.FlushAsync(ct).ConfigureAwait(false);
            await upload.File.DisposeAsync().ConfigureAwait(false);
            sha = Convert.ToHexStringLower(upload.Hash.GetHashAndReset());
            part = upload.PartPath;
            upload.Ended = true;
            lock (_sync)
            {
                _uploads.Remove(upload.Id);
            }
        }
        finally
        {
            upload.Gate.Release();
            upload.Hash.Dispose();
        }

        try
        {
            return await StoreAsync(upload, part, sha, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(part);
        }
    }

    /// <inheritdoc />
    public async Task CancelUploadAsync(string uploadId, CancellationToken ct)
    {
        Upload? upload;
        lock (_sync)
        {
            _uploads.TryGetValue(uploadId ?? string.Empty, out upload);
        }

        if (upload is null)
        {
            return;
        }

        await upload.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EndAsync(upload).ConfigureAwait(false);
        }
        finally
        {
            upload.Gate.Release();
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException">A setting is not allowed; the text says which.</exception>
    public async Task<VisionModel> UpdateAsync(VisionModel changed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(changed);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stored = Find(changed.Id) ?? throw new ArgumentException(ModelTexts.Gone);
            var name = CheckName(changed.Name, stored.Id);
            if (OutputFormats.KindOf(changed.Format) != stored.Kind)
            {
                throw new ArgumentException(ModelTexts.FormatOfOtherKind);
            }

            if (!Enum.IsDefined(changed.Fit) || !Enum.IsDefined(changed.Scaling) || !Enum.IsDefined(changed.Format))
            {
                throw new ArgumentException(ModelTexts.FormatOfOtherKind);
            }

            if (stored.FixedInput && (changed.InputWidth != stored.InputWidth || changed.InputHeight != stored.InputHeight))
            {
                throw new ArgumentException(ModelTexts.InputSizeFixed);
            }

            if (!stored.FixedInput && (!SideAllowed(changed.InputWidth) || !SideAllowed(changed.InputHeight)))
            {
                throw new ArgumentException(ModelTexts.InputSizeRule);
            }

            // A class list gives the names (its count must be the model's); without one the model's own names, edited as they come.
            var listId = string.IsNullOrEmpty(changed.ClassListId) ? null : changed.ClassListId;
            IReadOnlyList<string> names;
            if (listId is not null)
            {
                var list = FindClassList(listId) ?? throw new ArgumentException(ModelTexts.ClassListGone);
                var classes = stored.ClassNames.Count;
                if (classes > 0 && list.Names.Count != classes)
                {
                    throw new ArgumentException(ModelTexts.ClassListDoesNotFit(list.Names.Count, classes));
                }

                names = list.Names;
            }
            else
            {
                if (changed.ClassNames.Count > VisionLimits.MaxClasses)
                {
                    throw new ArgumentException(ModelTexts.TooManyLabels);
                }

                if (changed.ClassNames.Any(n => (n ?? string.Empty).Trim().Length > VisionLimits.MaxNameLength))
                {
                    throw new ArgumentException(ModelTexts.ClassNameTooLong);
                }

                names = [.. changed.ClassNames.Select((n, i) => ClassNames.Clean(n, i))];
            }

            var saved = stored with
            {
                Name = name,
                Format = changed.Format,
                Fit = changed.Fit,
                Scaling = changed.Scaling,
                InputWidth = changed.InputWidth,
                InputHeight = changed.InputHeight,
                ClassListId = listId,
                ClassNames = names,
            };
            await SaveAsync(saved, ct).ConfigureAwait(false);
            return saved;
        }
        finally
        {
            _write.Release();
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">Pipelines use it ("Used by 2 pipelines: Gate 1, Lane 2.").</exception>
    /// <exception cref="ArgumentException">There is no such model.</exception>
    public async Task DeleteAsync(string id, IReadOnlyList<string> usedBy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(usedBy);
        if (usedBy.Count > 0)
        {
            throw new InvalidOperationException(ModelTexts.UsedBy(usedBy));
        }

        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var model = Find(id) ?? throw new ArgumentException(ModelTexts.Gone);
            await _records.SetManyAsync(Collection, [new(model.Id, null)], ct).ConfigureAwait(false);
            lock (_sync)
            {
                _models.RemoveAll(m => m.Id == model.Id);
            }

            TryDelete(FilePath(model.Id));
        }
        finally
        {
            _write.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        List<Upload> uploads;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            uploads = [.. _uploads.Values];
            _uploads.Clear();
        }

        foreach (var upload in uploads)
        {
            upload.Idle?.Dispose();
            upload.Ended = true;
            upload.File.Dispose();
            upload.Hash.Dispose();
            TryDelete(upload.PartPath);
        }

        _write.Dispose();
        _checking.Dispose();
    }

    /// <summary>Checks the finished file and stores it: a Missing model of the same SHA-256 comes back, a new one is added.</summary>
    private async Task<ModelCheck> StoreAsync(Upload upload, string part, string sha, CancellationToken ct)
    {
        var same = All.FirstOrDefault(m => m.Sha256 == sha);
        if (same is { Missing: false })
        {
            return new ModelCheck(null, ModelTexts.AlreadyStored(same.Name));
        }

        // One check at a time: a session of a large model takes its memory.
        (CheckedModel? Model, string? Refusal) result;
        await _checking.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            result = await Task.Run(() => _check(part, upload.Labels, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _checking.Release();
        }

        if (result.Model is not { } facts)
        {
            return new ModelCheck(null, result.Refusal ?? ModelTexts.CannotRun(ModelTexts.NotOnnx));
        }

        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (All.FirstOrDefault(m => m.Sha256 == sha && !m.Missing) is { } stored)
            {
                // The same file finished its upload twice at once.
                return new ModelCheck(null, ModelTexts.AlreadyStored(stored.Name));
            }

            VisionModel model;
            if (Find(same?.Id) is { Missing: true } missing)
            {
                // The same file again: the model comes back with the user's settings.
                model = missing with { Missing = false, CpuMs = facts.CpuMs, GpuMs = null, SizeBytes = upload.Size };
            }
            else
            {
                if (All.Count >= VisionLimits.MaxModels)
                {
                    return new ModelCheck(null, ModelTexts.TooMany);
                }

                model = new VisionModel
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = FreeName(Path.GetFileNameWithoutExtension(upload.FileName)),
                    Kind = OutputFormats.KindOf(facts.Format),
                    Format = facts.Format,
                    Fit = OutputFormats.DefaultFit(facts.Format),
                    Scaling = facts.Scaling ?? OutputFormats.DefaultScaling(facts.Format),
                    InputWidth = facts.InputWidth,
                    InputHeight = facts.InputHeight,
                    FixedInput = facts.FixedInput,

                    // Names of the model or of its labels file are its own; else a built-in list by the class count.
                    ClassListId = facts.NamesFrom == NamesSource.None ? Models.ClassLists.ForClassCount(facts.ClassNames.Count)?.Id : null,
                    ClassNames = facts.NamesFrom == NamesSource.None && Models.ClassLists.ForClassCount(facts.ClassNames.Count) is { } builtIn ? builtIn.Names : facts.ClassNames,
                    SizeBytes = upload.Size,
                    Sha256 = sha,
                    CpuMs = facts.CpuMs,
                    UploadedAt = _time.GetUtcNow(),
                };
            }

            File.Move(part, FilePath(model.Id), overwrite: true);
            try
            {
                await SaveAsync(model, ct).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(FilePath(model.Id));
                throw;
            }

            return new ModelCheck(model, null);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Writes the record and the copy in memory (inside <see cref="_write"/>), then tells the change.</summary>
    private async Task SaveAsync(VisionModel model, CancellationToken ct)
    {
        await _records.SetManyAsync(Collection, [new(model.Id, VisionJson.Serialize(model with { Id = string.Empty, Missing = false }))], ct).ConfigureAwait(false);
        lock (_sync)
        {
            var index = _models.FindIndex(m => m.Id == model.Id);
            if (index >= 0)
            {
                _models[index] = model;
            }
            else
            {
                _models.Add(model);
            }

            _models = Sorted(_models);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string CheckName(string? name, string? id)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new ArgumentException(ModelTexts.NameMissing);
        }

        if (trimmed.Length > VisionLimits.MaxNameLength)
        {
            throw new ArgumentException(ModelTexts.NameTooLong);
        }

        if (All.FirstOrDefault(m => m.Id != id && string.Equals(m.Name, trimmed, StringComparison.OrdinalIgnoreCase)) is { } other)
        {
            throw new ArgumentException(ModelTexts.NameTaken(other.Name));
        }

        return trimmed;
    }

    /// <summary>The file's name as the model's name, made unique: "yolo11n", "yolo11n 2", ...</summary>
    private string FreeName(string fileName)
    {
        var name = fileName.Trim();
        if (name.Length == 0)
        {
            name = "Model";
        }

        if (name.Length > VisionLimits.MaxNameLength - 4)
        {
            name = name[..(VisionLimits.MaxNameLength - 4)].TrimEnd();
        }

        var taken = All.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(name))
        {
            return name;
        }

        for (var i = 2; ; i++)
        {
            var candidate = string.Create(CultureInfo.InvariantCulture, $"{name} {i}");
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool SideAllowed(int side) => side is >= ModelLimits.MinInputSide and <= ModelLimits.MaxInputSide && side % 32 == 0;

    private Upload Running(string uploadId)
    {
        lock (_sync)
        {
            return _uploads.TryGetValue(uploadId ?? string.Empty, out var upload) ? upload : throw new ArgumentException(ModelTexts.UploadGone);
        }
    }

    /// <summary>An upload without a chunk for <see cref="VisionLimits.UploadIdleTimeout"/> ends.</summary>
    private async Task EndIdleAsync(string id)
    {
        Upload? upload;
        lock (_sync)
        {
            _uploads.TryGetValue(id, out upload);
        }

        if (upload is null)
        {
            return;
        }

        await upload.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!upload.Ended)
            {
                LogUploadIdle(_logger, upload.FileName);
                await EndAsync(upload).ConfigureAwait(false);
            }
        }
        finally
        {
            upload.Gate.Release();
        }
    }

    /// <summary>Ends an upload (inside its gate): the timer, the file and its part on disk.</summary>
    private async Task EndAsync(Upload upload)
    {
        if (upload.Ended)
        {
            return;
        }

        upload.Ended = true;
        upload.Idle?.Dispose();
        lock (_sync)
        {
            _uploads.Remove(upload.Id);
        }

        await upload.File.DisposeAsync().ConfigureAwait(false);
        upload.Hash.Dispose();
        TryDelete(upload.PartPath);
    }

    private string FilePath(string id) => Path.Combine(_folder, id + ".onnx");

    private static List<ClassList> SortedLists(IEnumerable<ClassList> lists) =>
        [.. lists.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Id, StringComparer.Ordinal)];

    private static List<VisionModel> Sorted(IEnumerable<VisionModel> models) =>
        [.. models.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Id, StringComparer.Ordinal)];

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            LogNotDeleted(_logger, path, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogNotDeleted(_logger, path, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vision Center: the stored model {Key} could not be read and is left out")]
    private static partial void LogUnreadable(ILogger logger, string key, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vision Center: the model {Model} used the class list {ClassListId}, which is not there; it keeps its names as its own")]
    private static partial void LogListGone(ILogger logger, string model, string classListId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Vision Center: the upload of {FileName} ended, no part came for a minute")]
    private static partial void LogUploadIdle(ILogger logger, string fileName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Vision Center: the file {Path} could not be deleted: {Reason}")]
    private static partial void LogNotDeleted(ILogger logger, string path, string reason);

    /// <summary>An upload in progress: its file, the hash so far and the time of its last chunk.</summary>
    private sealed class Upload(string id, string fileName, long size, string? labels, FileStream file, string partPath)
    {
        public string PartPath { get; } = partPath;

        public string Id { get; } = id;

        public string FileName { get; } = fileName;

        public long Size { get; } = size;

        public string? Labels { get; } = labels;

        public FileStream File { get; } = file;

        public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public ITimer? Idle { get; set; }

        public long Written { get; set; }

        public bool Ended { get; set; }
    }
}
