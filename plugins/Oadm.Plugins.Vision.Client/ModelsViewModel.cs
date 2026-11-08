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

/// <summary>A row of the Models card: a stored model or an upload on its way.</summary>
public abstract class ModelsRow : ObservableObject;

/// <summary>A stored model: name, its line (kind, input, classes, size, pipelines) and the ⋯ menu.</summary>
public sealed partial class ModelRow : ModelsRow
{
    public ModelRow(VisionModel model, IReadOnlyList<string> usedBy) => Apply(model, usedBy);

    [ObservableProperty]
    public partial VisionModel Model { get; private set; } = new();

    [ObservableProperty]
    public partial string Name { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Line { get; private set; } = string.Empty;

    public IReadOnlyList<string> UsedBy { get; private set; } = [];

    public bool IsMissing => Model.Missing;

    public static string MissingText => VisionTexts.Missing;

    public static string MissingTip => VisionTexts.MissingTip;

    public void Apply(VisionModel model, IReadOnlyList<string> usedBy)
    {
        ArgumentNullException.ThrowIfNull(model);
        Model = model;
        UsedBy = usedBy ?? [];
        Name = model.Name;
        Line = VisionTexts.ModelLine(model, UsedBy.Count);
        OnPropertyChanged(nameof(IsMissing));
    }
}

/// <summary>An upload in the Models card: progress and Cancel in the row, then the model check's ring, a refusal in the row.</summary>
public sealed partial class UploadRow(string fileName) : ModelsRow, IDisposable
{
    private CancellationTokenSource? _cancel;

    public string FileName { get; } = fileName;

    /// <summary>0 to 1 of the file sent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(StateText))]
    public partial double Progress { get; set; }

    public double Percent => Progress * 100;

    /// <summary>The server checks the model (the ring).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsSending))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsChecking { get; set; }

    /// <summary>Why the model was not kept ("This model cannot run here: ..."); null while it goes on.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRefusal), nameof(IsSending), nameof(IsWorking))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial string? Refusal { get; set; }

    public bool HasRefusal => Refusal is not null;

    /// <summary>The bar shows while the file goes up.</summary>
    public bool IsSending => !IsChecking && Refusal is null;

    public bool IsWorking => Refusal is null;

    public string StateText => IsChecking ? VisionTexts.CheckingModel : VisionTexts.Uploading(Progress);

    /// <summary>Raised by Dismiss: the row goes.</summary>
    public event EventHandler? Dismissed;

    internal CancellationToken Start()
    {
        _cancel = new CancellationTokenSource();
        return _cancel.Token;
    }

    internal void Finish()
    {
        _cancel?.Dispose();
        _cancel = null;
    }

    private bool CanCancel() => _cancel is not null && !IsChecking && Refusal is null;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancel?.Cancel();

    [RelayCommand]
    private void Dismiss() => Dismissed?.Invoke(this, EventArgs.Empty);

    internal void NotifyCancel() => CancelCommand.NotifyCanExecuteChanged();

    public void Dispose() => Finish();
}

/// <summary>
/// The Models card (docs/spec/vision-center.md "Models"), for users with Manage vision models: Upload model sends the file in
/// chunks of the server's size with its progress and Cancel in the row, then the model check (a ring in the row); a refusal stays
/// in the row in the server's words. The ⋯ menu of a model: Rename, Classes, Delete in red (refused while pipelines use it).
/// </summary>
public sealed partial class ModelsViewModel : ObservableObject
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
    [NotifyCanExecuteChangedFor(nameof(UploadCommand))]
    public partial bool MayManage { get; private set; }

    public RangeObservableCollection<ModelsRow> Rows { get; } = [];

    /// <summary>The stored models (every user: the forms offer them).</summary>
    public IReadOnlyList<VisionModel> Models { get; private set; } = [];

    [ObservableProperty]
    public partial bool HasRows { get; private set; }

    public void ApplyPermissions() => MayManage = _ctx.Has(ModelsPermissionKey);

    public async Task LoadAsync(CancellationToken ct)
    {
        var json = await _ctx.InvokeAsync(VisionMethods.ListModels, "{}", ct).ConfigureAwait(true);
        Apply(VisionJson.Deserialize<ModelList>(json));
    }

    /// <summary>The list (listModels, the models event): rows updated in place by id, uploads stay at the end.</summary>
    public void Apply(ModelList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        Models = list.Models;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<ModelsRow>();
        foreach (var model in list.Models.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            ids.Add(model.Id);
            var used = list.UsedBy.GetValueOrDefault(model.Id) ?? [];
            if (_rows.TryGetValue(model.Id, out var row))
            {
                row.Apply(model, used);
            }
            else
            {
                row = new ModelRow(model, used);
                _rows[model.Id] = row;
            }

            rows.Add(row);
        }

        foreach (var gone in _rows.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _rows.Remove(gone);
        }

        rows.AddRange(Rows.OfType<UploadRow>());
        Rows.ReplaceAll(rows);
        HasRows = Rows.Count > 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool CanUpload() => MayManage;

    /// <summary>Upload model: the file picker (the model, and a labels file with it if wanted), then the upload.</summary>
    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task UploadAsync()
    {
        if (Dialogs is { } dialogs && await dialogs.PickModelFilesAsync().ConfigureAwait(true) is { Count: > 0 } files)
        {
            await UploadFilesAsync(files).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Sends the model file (the first .onnx, else the first file) with the text of a .txt file as its labels: chunks of the
    /// server's size, progress after each, Cancel between them; then the model check. The row goes once the model is stored.
    /// </summary>
    public async Task UploadFilesAsync(IReadOnlyList<IModelFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var file = files.FirstOrDefault(f => f.Name.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)) ?? (files.Count > 0 ? files[0] : null);
        if (file is null)
        {
            return;
        }

        string? labels = null;
        if (files.FirstOrDefault(f => !ReferenceEquals(f, file) && f.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) is { } labelsFile)
        {
            using var reader = new StreamReader(await labelsFile.OpenReadAsync().ConfigureAwait(true));
            labels = await reader.ReadToEndAsync().ConfigureAwait(true);
        }

        var row = new UploadRow(file.Name);
        row.Dismissed += (_, _) => RemoveRow(row);
        Rows.Add(row);
        HasRows = true;
        var ct = row.Start();
        row.NotifyCancel();
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
                    row.Progress = file.Length > 0 ? Math.Min(1, (double)offset / file.Length) : 1;
                }
            }

            ct.ThrowIfCancellationRequested();
            row.IsChecking = true;
            var finishJson = await _ctx.InvokeAsync(VisionMethods.FinishUpload, VisionJson.Serialize(new UploadRequest { UploadId = uploadId }), CancellationToken.None)
                .ConfigureAwait(true);
            var finish = VisionJson.Deserialize<FinishUploadReply>(finishJson);
            uploadId = null;
            if (finish.Refusal is { } refusal || finish.Model is null)
            {
                row.IsChecking = false;
                row.Refusal = finish.Refusal ?? "This model cannot run here.";
                return;
            }

            RemoveRow(row);
            Apply(new ModelList
            {
                Models = [.. Models.Where(m => m.Id != finish.Model.Id), finish.Model],
                UsedBy = _rows.Values.ToDictionary(r => r.Model.Id, r => r.UsedBy, StringComparer.Ordinal),
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            RemoveRow(row);
            await CancelOnServerAsync(uploadId).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The server's reason in the row (over the limits, a refused file).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            row.IsChecking = false;
            row.Refusal = ServerErrors.Message(ex);
            await CancelOnServerAsync(uploadId).ConfigureAwait(true);
        }
        finally
        {
            row.Finish();
            row.NotifyCancel();
        }
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

    private void RemoveRow(ModelsRow row)
    {
        Rows.Remove(row);
        HasRows = Rows.Count > 0;
    }

    /// <summary>The entries of a model's ⋯ menu (ui:RowActions): Rename, Classes, then Delete in red.</summary>
    public IReadOnlyList<IssuedCertificateAction> ActionsOf(ModelRow row) =>
    [
        new(VisionTexts.Rename, RenameCommand, row),
        new(VisionTexts.Classes, ClassesCommand, row),
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

    [RelayCommand]
    private async Task ClassesAsync(ModelRow? row)
    {
        if (row is null || Dialogs is not { } dialogs)
        {
            return;
        }

        var form = ModelEditViewModel.ForClasses(row.Model);
        if (await dialogs.EditModelAsync(form).ConfigureAwait(true))
        {
            await UpdateAsync(VisionTexts.ModelClasses, row.Model with { ClassNames = form.ClassNames }).ConfigureAwait(true);
        }
    }

    private async Task UpdateAsync(string title, VisionModel changed)
    {
        var error = await GuardedRun.RunAsync(async () =>
        {
            var json = await _ctx.InvokeAsync(VisionMethods.UpdateModel, VisionJson.Serialize(new UpdateModelRequest { Model = changed }), CancellationToken.None)
                .ConfigureAwait(true);
            var model = VisionJson.Deserialize<VisionModel>(json);
            Apply(new ModelList
            {
                Models = [.. Models.Select(m => m.Id == model.Id ? model : m)],
                UsedBy = _rows.Values.ToDictionary(r => r.Model.Id, r => r.UsedBy, StringComparer.Ordinal),
            });
        }).ConfigureAwait(true);
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
                UsedBy = _rows.Values.Where(r => r != row).ToDictionary(r => r.Model.Id, r => r.UsedBy, StringComparer.Ordinal),
            });
        }).ConfigureAwait(true);
        if (error is not null)
        {
            await _ctx.ShowMessageAsync(VisionTexts.DeleteModel, error).ConfigureAwait(true);
        }
    }
}

/// <summary>
/// The dialog of a model's Rename (one Name field: required, unique, at most 64 characters) or Classes (one name per line, as
/// many lines as the model has classes); the error below the field, the main button off while there is one.
/// </summary>
public sealed partial class ModelEditViewModel : ValidatingViewModel
{
    private readonly Func<string, bool> _taken;
    private readonly int _classCount;

    private ModelEditViewModel(bool isRename, string name, string classes, int classCount, Func<string, bool> taken)
    {
        IsRename = isRename;
        _taken = taken;
        _classCount = classCount;
        Name = name;
        ClassesText = classes;
        if (isRename)
        {
            Validation.Rule(nameof(Name), () => Name.Trim().Length == 0 ? GraphTexts.EnterName
                : Name.Trim().Length > VisionLimits.MaxNameLength ? GraphTexts.NameTooLong
                : _taken(Name.Trim()) ? VisionTexts.ModelNameTaken
                : null);
        }
        else
        {
            Validation.Rule(nameof(ClassesText), () => ClassNames.Count != _classCount || ClassNames.Any(n => n.Length == 0) ? VisionTexts.WrongClassCount : null);
        }

        Validation.Validate();
    }

    public static ModelEditViewModel ForRename(VisionModel model, Func<string, bool> taken)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new(true, model.Name, string.Empty, 0, taken ?? throw new ArgumentNullException(nameof(taken)));
    }

    public static ModelEditViewModel ForClasses(VisionModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new(false, model.Name, string.Join(Environment.NewLine, model.ClassNames), model.ClassNames.Count, _ => false);
    }

    /// <summary>OK (true) or Cancel (false): the view closes the window.</summary>
    public event EventHandler<bool>? CloseRequested;

    public bool IsRename { get; }

    public bool IsClasses => !IsRename;

    public string Title => IsRename ? VisionTexts.RenameModel : VisionTexts.ModelClasses;

    public string ConfirmText => IsRename ? VisionTexts.Rename : VisionTexts.Save;

    public static int MaxNameLength => VisionLimits.MaxNameLength;

    public static string ClassesHint => VisionTexts.ClassesHint;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OkCommand))]
    public partial string ClassesText { get; set; }

    /// <summary>The class names as the lines say, trimmed (a trailing empty line left out).</summary>
    public IReadOnlyList<string> ClassNames
    {
        get
        {
            var lines = ClassesText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).ToList();
            while (lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            return lines;
        }
    }

    private bool CanOk() => IsFormValid;

    [RelayCommand(CanExecute = nameof(CanOk))]
    private void Ok()
    {
        Validation.ShowAll();
        if (IsFormValid)
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    protected override void OnValidationChanged() => OkCommand.NotifyCanExecuteChanged();
}
