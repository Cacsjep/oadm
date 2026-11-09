using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.Vision.Client;

/// <summary>A file the user chose for an upload (the view wraps the picker's file, tests hand bytes).</summary>
public interface IModelFile
{
    string Name { get; }

    long Length { get; }

    Task<Stream> OpenReadAsync();
}

/// <summary>A row of the Models card.</summary>
public abstract class ModelsRow : ObservableObject;

/// <summary>A stored model: name, its line (kind, input, classes, size, pipelines) and the ⋯ menu.</summary>
public sealed partial class ModelRow : ModelsRow
{
    public ModelRow(VisionModel model, IReadOnlyList<string> usedBy, string? listName = null) => Apply(model, usedBy, listName);

    [ObservableProperty]
    public partial VisionModel Model { get; private set; } = new();

    [ObservableProperty]
    public partial string Name { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Line { get; private set; } = string.Empty;

    public IReadOnlyList<string> UsedBy { get; private set; } = [];

    /// <summary>The line's tooltip: the whole line, the time of one call on the CPU and on the card, the SHA-256.</summary>
    [ObservableProperty]
    public partial string Details { get; private set; } = string.Empty;

    /// <summary>A change of the model is being saved: a ring in place of its menu.</summary>
    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    public bool IsMissing => Model.Missing;

    public static string MissingText => VisionTexts.Missing;

    public static string MissingTip => VisionTexts.MissingTip;

    public void Apply(VisionModel model, IReadOnlyList<string> usedBy, string? listName = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;
        UsedBy = usedBy ?? [];
        Name = model.Name;
        Line = VisionTexts.ModelLine(model, UsedBy.Count, listName);
        Details = VisionTexts.ModelDetails(model, UsedBy);
        OnPropertyChanged(nameof(IsMissing));
    }
}

/// <summary>
/// The Models card (docs/spec/vision-center.md "Models"), for users with Manage vision models: Import model opens the wizard
/// (<see cref="ModelImportViewModel"/>), which sends the file in chunks of the server's size, then the model check. The ⋯ menu of a model: Rename, Class list, Delete in red (refused while pipelines use it).
/// Below the models the class lists ("Class lists"): the built-in ones to view, the user's to edit and delete.
/// </summary>
public sealed partial class ModelsViewModel : ObservableObject, IModelImporter
{
    /// <summary>"oadm.vision:models".</summary>
    public static readonly string ModelsPermissionKey = VisionInfo.PluginId + ":" + VisionInfo.ModelsPermission;

    private readonly ICorePluginClientContext _ctx;
    private readonly Dictionary<string, ModelRow> _rows = new(StringComparer.Ordinal);

    public ModelsViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        MayManage = ctx.Has(ModelsPermissionKey);
    }

    /// <summary>The list changed: the pipelines check their model nodes again.</summary>
    public event EventHandler? Changed;

    public IVisionDialogs? Dialogs { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial bool MayManage { get; private set; }

    public RangeObservableCollection<ModelsRow> Rows { get; } = [];

    /// <summary>The class lists: the built-in ones first, then the user's by name.</summary>
    public RangeObservableCollection<ClassListRow> ClassLists { get; } = [];

    /// <summary>The stored class lists (the forms name a model's list).</summary>
    public IReadOnlyList<ClassList> Lists { get; private set; } = [];

    private IReadOnlyDictionary<string, IReadOnlyList<string>> _modelsUsedBy = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>"COCO, 91 ids" for a model's list, "Own names" without one (or a list no longer there).</summary>
    public string ClassListNameOf(VisionModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.ClassListId is { } id && Lists.FirstOrDefault(l => l.Id == id) is { } list ? list.Name : VisionTexts.OwnNames;
    }

    /// <summary>The stored models (every user: the forms offer them).</summary>
    public IReadOnlyList<VisionModel> Models { get; private set; } = [];

    [ObservableProperty]
    public partial bool HasRows { get; private set; }

    public void ApplyPermissions() => MayManage = _ctx.Has(ModelsPermissionKey);

    public async Task LoadAsync(CancellationToken ct)
    {
        await LoadClassListsAsync(ct).ConfigureAwait(true);
        var json = await _ctx.InvokeAsync(VisionMethods.ListModels, "{}", ct).ConfigureAwait(true);
        Apply(VisionJson.Deserialize<ModelList>(json));
    }

    /// <summary>The class lists (listClassLists); an older server without them: none.</summary>
    public async Task LoadClassListsAsync(CancellationToken ct)
    {
        try
        {
            var json = await _ctx.InvokeAsync(VisionMethods.ListClassLists, "{}", ct).ConfigureAwait(true);
            ApplyClassLists(VisionJson.Deserialize<ClassListList>(json));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Without the lists the models keep their own names.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>The class lists: rows replaced once, the model lines name their list again.</summary>
    public void ApplyClassLists(ClassListList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        Lists = list.Lists;
        ClassLists.ReplaceAll(list.Lists
            .OrderByDescending(l => l.BuiltIn)
            .ThenBy(l => l.BuiltIn ? string.Empty : l.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(l => new ClassListRow(l, list.UsedBy.GetValueOrDefault(l.Id) ?? [])));
        foreach (var row in _rows.Values)
        {
            row.Apply(row.Model, row.UsedBy, ClassListNameOf(row.Model));
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The list (listModels, the models event): rows updated in place by id.</summary>
    public void Apply(ModelList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        Models = list.Models;
        _modelsUsedBy = list.UsedBy;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<ModelsRow>();
        foreach (var model in list.Models.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            ids.Add(model.Id);
            var used = list.UsedBy.GetValueOrDefault(model.Id) ?? [];
            if (_rows.TryGetValue(model.Id, out var row))
            {
                row.Apply(model, used, ClassListNameOf(model));
            }
            else
            {
                row = new ModelRow(model, used, ClassListNameOf(model));
                _rows[model.Id] = row;
            }

            rows.Add(row);
        }

        foreach (var gone in _rows.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _rows.Remove(gone);
        }

        Rows.ReplaceAll(rows);
        HasRows = Rows.Count > 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool CanUpload() => MayManage;

    /// <summary>Import model: the wizard (file, family, check, settings, classes); the card shows the model once imported.</summary>
    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task ImportAsync()
    {
        if (Dialogs is { } dialogs)
        {
            await dialogs.ImportModelAsync(new ModelImportViewModel(this, dialogs)).ConfigureAwait(true);
        }
    }

    /// <inheritdoc />
    public async Task<FinishUploadReply> SendAsync(IModelFile file, string? labels, Action<double> progress, Action checking, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(checking);
        string? uploadId = null;
        try
        {
            var beginJson = await _ctx.InvokeAsync(VisionMethods.BeginUpload,
                VisionJson.Serialize(new BeginUploadRequest { FileName = file.Name, Size = file.Length, Labels = labels }), ct).ConfigureAwait(true);
            var begin = VisionJson.Deserialize<BeginUploadReply>(beginJson);
            uploadId = begin.UploadId;
            var chunk = new byte[Math.Clamp(begin.ChunkBytes, 1, VisionLimits.UploadChunkBytes * 4)];
            await using (var stream = await file.OpenReadAsync().ConfigureAwait(true))
            {
                long offset = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var read = 0;
                    while (read < chunk.Length && await stream.ReadAsync(chunk.AsMemory(read), ct).ConfigureAwait(true) is var n and > 0)
                    {
                        read += n;
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    var request = new UploadChunkRequest { UploadId = uploadId, Offset = offset, Data = chunk[..read] };
                    await _ctx.InvokeAsync(VisionMethods.UploadChunk, VisionJson.Serialize(request), ct).ConfigureAwait(true);
                    offset += read;
                    progress(file.Length > 0 ? Math.Min(1, (double)offset / file.Length) : 1);
                }
            }

            ct.ThrowIfCancellationRequested();
            checking();
            var finishJson = await _ctx.InvokeAsync(VisionMethods.FinishUpload, VisionJson.Serialize(new UploadRequest { UploadId = uploadId }), CancellationToken.None)
                .ConfigureAwait(true);
            uploadId = null;
            var finish = VisionJson.Deserialize<FinishUploadReply>(finishJson);
            if (finish.Model is { } model && finish.Refusal is null)
            {
                Apply(new ModelList { Models = [.. Models.Where(m => m.Id != model.Id), model], UsedBy = _modelsUsedBy });
            }

            return finish;
        }
        catch (Exception)
        {
            await CancelOnServerAsync(uploadId).ConfigureAwait(true);
            throw;
        }
    }

    /// <inheritdoc />
    async Task<VisionModel> IModelImporter.UpdateAsync(VisionModel model)
    {
        var json = await _ctx.InvokeAsync(VisionMethods.UpdateModel, VisionJson.Serialize(new UpdateModelRequest { Model = model }), CancellationToken.None)
            .ConfigureAwait(true);
        var stored = VisionJson.Deserialize<VisionModel>(json);
        Apply(new ModelList { Models = [.. Models.Select(m => m.Id == stored.Id ? stored : m)], UsedBy = _modelsUsedBy });
        return stored;
    }

    /// <inheritdoc />
    async Task IModelImporter.DeleteAsync(string modelId)
    {
        await _ctx.InvokeAsync(VisionMethods.DeleteModel, VisionJson.Serialize(new ModelRequest { ModelId = modelId }), CancellationToken.None).ConfigureAwait(true);
        Apply(new ModelList { Models = [.. Models.Where(m => m.Id != modelId)], UsedBy = _modelsUsedBy });
    }

    private async Task CancelOnServerAsync(string? uploadId)
    {
        if (uploadId is null)
        {
            return;
        }

        try
        {
            await _ctx.InvokeAsync(VisionMethods.CancelUpload, VisionJson.Serialize(new UploadRequest { UploadId = uploadId }), CancellationToken.None)
                .ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The server ends an idle upload by itself.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>The entries of a model's ⋯ menu (ui:RowActions): Rename, Classes, then Delete in red.</summary>
    public IReadOnlyList<IssuedCertificateAction> ActionsOf(ModelRow row) =>
    [
        new(VisionTexts.Rename, RenameCommand, row),
        new(VisionTexts.ModelSettings, SettingsCommand, row),
        new(VisionTexts.ClassListTitle, ClassListCommand, row),
        IssuedCertificateAction.Separator,
        new(VisionTexts.Delete, DeleteCommand, row, IsDanger: true),
    ];

    [RelayCommand]
    private async Task RenameAsync(ModelRow? row)
    {
        if (row is null || Dialogs is not { } dialogs)
        {
            return;
        }

        var form = ModelEditViewModel.ForRename(row.Model, name => Models.Any(m => m.Id != row.Model.Id && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)));
        if (await dialogs.EditModelAsync(form).ConfigureAwait(true))
        {
            await UpdateAsync(VisionTexts.RenameModel, row.Model with { Name = form.Name.Trim() }).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Settings: format, fit, scaling and a free input size, through updateModel; a refusal of a field shows below it and the
    /// dialog opens again with what the user chose; another refusal in the host's message.
    /// </summary>
    [RelayCommand]
    private async Task SettingsAsync(ModelRow? row)
    {
        if (row is null || Dialogs is not { } dialogs)
        {
            return;
        }

        var form = new ModelSettingsViewModel(row.Model);
        while (await dialogs.EditModelSettingsAsync(form).ConfigureAwait(true))
        {
            var saving = RowOf(row.Model.Id);
            saving?.IsSaving = true;
            try
            {
                var json = await _ctx.InvokeAsync(VisionMethods.UpdateModel, VisionJson.Serialize(new UpdateModelRequest { Model = form.ToModel() }), CancellationToken.None)
                    .ConfigureAwait(true);
                var model = VisionJson.Deserialize<VisionModel>(json);
                saving?.IsSaving = false;
                Apply(new ModelList { Models = [.. Models.Select(m => m.Id == model.Id ? model : m)], UsedBy = _modelsUsedBy });
                return;
            }
#pragma warning disable CA1031 // The server's reason below its field, else in the host's message.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                var message = ServerErrors.Message(ex);
                saving?.IsSaving = false;
                if (!form.ShowRefusal(message))
                {
                    await _ctx.ShowMessageAsync(VisionTexts.ModelSettings, message).ConfigureAwait(true);
                    return;
                }
            }
        }
    }

    /// <summary>Class list: a list that fits the model's class count, or its own names.</summary>
    [RelayCommand]
    private async Task ClassListAsync(ModelRow? row)
    {
        if (row is null || Dialogs is not { } dialogs)
        {
            return;
        }

        var form = ModelEditViewModel.ForClassList(row.Model, Lists);
        if (await dialogs.EditModelAsync(form).ConfigureAwait(true))
        {
            await UpdateAsync(VisionTexts.ClassListTitle, row.Model with { ClassListId = form.ClassListId, ClassNames = form.ClassNames }).ConfigureAwait(true);
        }
    }

    // ---- Class lists ----

    /// <summary>The entries of a class list's ⋯ menu: View for a built-in one; Edit, then Delete in red, for the user's.</summary>
    public IReadOnlyList<IssuedCertificateAction> ActionsOf(ClassListRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.List.BuiltIn
            ? [new(VisionTexts.View, ViewClassListCommand, row)]
            : [new(VisionTexts.Edit, EditClassListCommand, row), IssuedCertificateAction.Separator, new(VisionTexts.Delete, DeleteClassListCommand, row, IsDanger: true)];
    }

    private bool ListNameTaken(string id, string name) =>
        Lists.Any(l => l.Id != id && string.Equals(l.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task NewClassListAsync()
    {
        if (Dialogs is { } dialogs)
        {
            await EditListAsync(dialogs, new ClassListEditViewModel(null, name => ListNameTaken(string.Empty, name))).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task EditClassListAsync(ClassListRow? row)
    {
        if (row is not null && Dialogs is { } dialogs)
        {
            ClearRefusals();
            await EditListAsync(dialogs, new ClassListEditViewModel(row.List, name => ListNameTaken(row.List.Id, name))).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ViewClassListAsync(ClassListRow? row)
    {
        if (row is not null && Dialogs is { } dialogs)
        {
            await dialogs.EditClassListAsync(new ClassListEditViewModel(row.List, _ => false, isReadOnly: true)).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The dialog until the server stores the list: a refusal of a field shows below it and the dialog opens again with what
    /// the user entered; another refusal in the host's message.
    /// </summary>
    private async Task EditListAsync(IVisionDialogs dialogs, ClassListEditViewModel form)
    {
        while (await dialogs.EditClassListAsync(form).ConfigureAwait(true))
        {
            SaveClassListReply reply;
            try
            {
                var json = await _ctx.InvokeAsync(VisionMethods.SaveClassList, VisionJson.Serialize(new SaveClassListRequest { ClassList = form.ToList() }), CancellationToken.None)
                    .ConfigureAwait(true);
                reply = VisionJson.Deserialize<SaveClassListReply>(json);
            }
#pragma warning disable CA1031 // The server's reason in the host's message.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                await _ctx.ShowMessageAsync(form.Title, ServerErrors.Message(ex)).ConfigureAwait(true);
                return;
            }

            if (reply.Errors.Count == 0 && reply.ClassList is { } saved)
            {
                var lists = Lists.Where(l => l.Id != saved.Id).Append(saved).ToList();
                var usedBy = ClassLists.ToDictionary(r => r.List.Id, r => r.UsedBy, StringComparer.Ordinal);
                ApplyClassLists(new ClassListList { Lists = lists, UsedBy = usedBy });
                await LoadModelsQuietlyAsync().ConfigureAwait(true); // the models that use it follow
                return;
            }

            if (form.ShowServerErrors(reply.Errors) is { } other)
            {
                await _ctx.ShowMessageAsync(form.Title, other).ConfigureAwait(true);
                return;
            }
        }
    }

    /// <summary>Delete (red): asks first; a refusal (a model uses it) stays in the row.</summary>
    [RelayCommand]
    private async Task DeleteClassListAsync(ClassListRow? row)
    {
        if (row is null)
        {
            return;
        }

        ClearRefusals();
        if (!await _ctx.ConfirmAsync(VisionTexts.DeleteClassList, VisionTexts.DeleteModelQuestion(row.Name), VisionTexts.Delete).ConfigureAwait(true))
        {
            return;
        }

        var error = await GuardedRun.RunAsync(async () =>
        {
            await _ctx.InvokeAsync(VisionMethods.DeleteClassList, VisionJson.Serialize(new ClassListRequest { ClassListId = row.List.Id }), CancellationToken.None)
                .ConfigureAwait(true);
            ApplyClassLists(new ClassListList
            {
                Lists = [.. Lists.Where(l => l.Id != row.List.Id)],
                UsedBy = ClassLists.Where(r => r != row).ToDictionary(r => r.List.Id, r => r.UsedBy, StringComparer.Ordinal),
            });
        }).ConfigureAwait(true);
        row.Refusal = error;
    }

    private void ClearRefusals()
    {
        foreach (var row in ClassLists)
        {
            row.Refusal = null;
        }
    }

    private async Task LoadModelsQuietlyAsync()
    {
        try
        {
            Apply(VisionJson.Deserialize<ModelList>(await _ctx.InvokeAsync(VisionMethods.ListModels, "{}", CancellationToken.None).ConfigureAwait(true)));
        }
#pragma warning disable CA1031 // The models event brings them later.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private ModelRow? RowOf(string id) => Rows.OfType<ModelRow>().FirstOrDefault(r => r.Model.Id == id);

    private async Task UpdateAsync(string title, VisionModel changed)
    {
        var saving = RowOf(changed.Id);
        saving?.IsSaving = true;
        var error = await GuardedRun.RunAsync(async () =>
        {
            var json = await _ctx.InvokeAsync(VisionMethods.UpdateModel, VisionJson.Serialize(new UpdateModelRequest { Model = changed }), CancellationToken.None)
                .ConfigureAwait(true);
            var model = VisionJson.Deserialize<VisionModel>(json);
            Apply(new ModelList
            {
                Models = [.. Models.Select(m => m.Id == model.Id ? model : m)],
                UsedBy = _modelsUsedBy,
            });
        }).ConfigureAwait(true);
        saving?.IsSaving = false;
        if (error is not null)
        {
            await _ctx.ShowMessageAsync(title, error).ConfigureAwait(true);
        }
    }

    /// <summary>Delete (red): asks first; the server refuses while pipelines use it ("Used by 2 pipelines: Gate 1, Lane 2.").</summary>
    [RelayCommand]
    private async Task DeleteAsync(ModelRow? row)
    {
        if (row is null || !await _ctx.ConfirmAsync(VisionTexts.DeleteModel, VisionTexts.DeleteModelQuestion(row.Name), VisionTexts.Delete).ConfigureAwait(true))
        {
            return;
        }

        var error = await GuardedRun.RunAsync(async () =>
        {
            await _ctx.InvokeAsync(VisionMethods.DeleteModel, VisionJson.Serialize(new ModelRequest { ModelId = row.Model.Id }), CancellationToken.None)
                .ConfigureAwait(true);
            Apply(new ModelList
            {
                Models = [.. Models.Where(m => m.Id != row.Model.Id)],
                UsedBy = _modelsUsedBy,
            });
        }).ConfigureAwait(true);
        if (error is not null)
        {
            await _ctx.ShowMessageAsync(VisionTexts.DeleteModel, error).ConfigureAwait(true);
        }
    }
}
