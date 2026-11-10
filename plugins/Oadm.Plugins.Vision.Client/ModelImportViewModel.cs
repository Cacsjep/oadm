using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Vision.Api;
using Oadm.Plugins.Vision.Graph;
using Oadm.Plugins.Vision.Model;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.Vision.Client;

/// <summary>What the Import model wizard needs from the Models card: the upload, the model's changes, the stored lists.</summary>
public interface IModelImporter
{
    /// <summary>
    /// Sends <paramref name="file"/> with the <paramref name="labels"/> text in the server's chunks (<paramref name="progress"/>
    /// 0 to 1 after each), then the model check (<paramref name="checking"/> first): the stored model or the refusal. Throws on
    /// a server error or <paramref name="ct"/> (the upload ends on the server).
    /// </summary>
    Task<FinishUploadReply> SendAsync(IModelFile file, string? labels, Action<double> progress, Action checking, CancellationToken ct);

    /// <summary>updateModel: the stored model as the server keeps it (the card follows). Throws with the server's refusal.</summary>
    Task<VisionModel> UpdateAsync(VisionModel model);

    /// <summary>deleteModel of a model the wizard stored and the user did not keep (the card follows).</summary>
    Task DeleteAsync(string modelId);

    /// <summary>The stored models (a name must be free; a model already stored before the upload came back).</summary>
    IReadOnlyList<VisionModel> Models { get; }

    IReadOnlyList<ClassList> Lists { get; }
}

/// <summary>
/// Import model (docs/spec/vision-center.md "Import model"), a wizard for users who are no experts. Model: a drawing of what
/// happens and the file only; Next sends it (progress and Cancel in the step), then the model check, a refusal below the file.
/// Settings: everything the check found ("Model checked" and its line), the name, the model family it found (another family
/// sets its format, fit and scaling), then format, fit and scaling each with its drawing and what the chosen value does.
/// Labels: the class list or own names, which a labels file can fill. Import saves name, settings and labels in one
/// updateModel. The model is stored from the check on: Cancel (or closing the window) deletes it again, unless it was a
/// Missing model that came back.
/// </summary>
public sealed partial class ModelImportViewModel : ValidatingViewModel, IDisposable
{
    private static readonly string[] StepNames = [VisionTexts.StepModel, VisionTexts.StepSettings, VisionTexts.StepLabels];

    private readonly IModelImporter _importer;
    private readonly IVisionDialogs _dialogs;
    private CancellationTokenSource? _sending;
    private IModelFile? _storedFrom;
    private bool _cameBack;
    private bool _finished;
    private bool _settingFamily;

    public ModelImportViewModel(IModelImporter importer, IVisionDialogs dialogs)
    {
        _importer = importer ?? throw new ArgumentNullException(nameof(importer));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        Steps = [.. StepNames.Select((name, i) => new WizardStepItem(i, name, () => Step, CanOpen, GoTo))];
        Validation
            .Rule(nameof(FileName), () => File is null ? VisionTexts.ChooseModelFileFirst : FileError)
            .Rule(nameof(Name), () => Stored is null ? null
                : Name.Trim().Length == 0 ? GraphTexts.EnterName
                : Name.Trim().Length > VisionLimits.MaxNameLength ? GraphTexts.NameTooLong
                : _importer.Models.Any(m => m.Id != Stored.Id && string.Equals(m.Name, Name.Trim(), StringComparison.OrdinalIgnoreCase)) ? VisionTexts.ModelNameTaken
                : null);
        Validation.Validate();
    }

    /// <summary>Import (true) or Cancel (false): the view closes the window.</summary>
    public event EventHandler<bool>? CloseRequested;

    public static string Title => VisionTexts.ImportModel;

    public IReadOnlyList<WizardStepItem> Steps { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModelStep), nameof(IsSettingsStep), nameof(IsLabelsStep), nameof(CanGoBack), nameof(IsLastStep), nameof(IsNotLastStep), nameof(StepProblem))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand), nameof(ImportCommand))]
    public partial int Step { get; private set; }

    public bool IsModelStep => Step == 0;

    public bool IsSettingsStep => Step == 1;

    public bool IsLabelsStep => Step == 2;

    public bool CanGoBack => Step > 0 && !IsWorking;

    public bool IsLastStep => Step == 2;

    public bool IsNotLastStep => !IsLastStep;

    // ---- Step 1: the model file ----

    public static string ModelIntro => VisionTexts.ImportIntro;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(FileDetails))]
    public partial IModelFile? File { get; private set; }

    public string? FileName => File?.Name;

    public string? FileDetails => File is { } file ? VisionTexts.Size(file.Length) : null;

    /// <summary>The file's error as shown (below the file button): none chosen after Next, or the server's refusal.</summary>
    public string? FileProblem => ErrorOf(nameof(FileName));

    /// <summary>Why the server did not keep the file ("This model cannot run here: ..."), below the file.</summary>
    [ObservableProperty]
    public partial string? FileError { get; private set; }

    /// <summary>The file goes up or the check runs: the progress row, everything else waits.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand), nameof(BackCommand), nameof(ImportCommand), nameof(ChooseFileCommand), nameof(LoadLabelsCommand))]
    public partial bool IsWorking { get; private set; }

    public bool IsIdle => !IsWorking;

    [ObservableProperty]
    public partial double Progress { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    /// <summary>The model the check stored.</summary>
    public VisionModel? Stored { get; private set; }

    // ---- Step 2: what the check found ----

    public static string ModelDetected => VisionTexts.ModelDetected;

    /// <summary>"Detection · 640x640 · 80 classes", below Model detected.</summary>
    [ObservableProperty]
    public partial string? Found { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    public static int MaxNameLength => VisionLimits.MaxNameLength;

    /// <summary>The families of the model's kind.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<ModelFamily> Families { get; private set; } = [];

    /// <summary>The family the check found; another one sets its format, fit and scaling below.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FamilyFound), nameof(FamilyChanged), nameof(FamilyHint))]
    public partial ModelFamily? Family { get; set; }

    /// <summary>The family the check found (null before a check).</summary>
    public ModelFamily? FoundFamily { get; private set; }

    /// <summary>The family select holds what the check found (a green check).</summary>
    public bool FamilyFound => FoundFamily is not null && Family == FoundFamily;

    /// <summary>The user chose another family than the check found (a warning, the found one below).</summary>
    public bool FamilyChanged => FoundFamily is not null && Family != FoundFamily;

    public string? FamilyHint => FamilyChanged ? VisionTexts.CheckFound(FoundFamily!.Name) : null;

    [ObservableProperty]
    public partial ModelSettingsViewModel? Settings { get; private set; }

    // ---- Step 3: the labels ----

    public static string LabelsHint => VisionTexts.LabelsStepHint;

    [ObservableProperty]
    public partial ModelEditViewModel? ClassList { get; private set; }

    /// <summary>A labels file that could not be read, below Load labels file.</summary>
    [ObservableProperty]
    public partial string? LabelsError { get; private set; }

    /// <summary>A refusal of the import that belongs to no field (below the step's form).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServerError))]
    public partial string? ServerError { get; private set; }

    public bool HasServerError => ServerError is not null;

    /// <summary>Why Next or Import is off (their tooltip).</summary>
    public string? StepProblem => Step switch
    {
        0 => ErrorOf(nameof(FileName)) ?? (File is null ? VisionTexts.ChooseModelFileFirst : null),
        1 => Validation.ErrorOf(nameof(Name)) ?? Settings?.FormError,
        _ => ClassList?.Error,
    };

    /// <summary>The model as imported (after Import).</summary>
    public VisionModel? Result { get; private set; }

    partial void OnFileChanged(IModelFile? value) => FileError = null;

    partial void OnFileErrorChanged(string? value) => Validation.Validate();

    partial void OnFamilyChanged(ModelFamily? value)
    {
        if (_settingFamily || value is null || Settings is not { } settings)
        {
            return;
        }

        settings.Format = settings.Formats.FirstOrDefault(f => f.Value == value.Format) ?? settings.Format;
        settings.Fit = ModelSettingsViewModel.Fits.First(f => f.Value == value.Fit);
        settings.Scaling = ModelSettingsViewModel.Scalings.First(s => s.Value == value.Scaling);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ChooseFileAsync()
    {
        if (await _dialogs.PickModelFileAsync(labels: false).ConfigureAwait(true) is { } file)
        {
            File = file;
            Validation.ShowAll(nameof(FileName));
        }
    }

    /// <summary>The file the tests (and a drop) hand in directly.</summary>
    public void SetFile(IModelFile file) => File = file ?? throw new ArgumentNullException(nameof(file));

    /// <summary>Load labels file: its lines become the model's own names (one per class; another count shows below them).</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task LoadLabelsAsync()
    {
        if (await _dialogs.PickModelFileAsync(labels: true).ConfigureAwait(true) is { } file)
        {
            await LoadLabelsAsync(file).ConfigureAwait(true);
        }
    }

    /// <summary>The lines of <paramref name="file"/> as the own names (tests hand the file).</summary>
    public async Task LoadLabelsAsync(IModelFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (ClassList is not { } list)
        {
            return;
        }

        LabelsError = null;
        try
        {
            using var reader = new StreamReader(await file.OpenReadAsync().ConfigureAwait(true));
            var text = await reader.ReadToEndAsync().ConfigureAwait(true);
            list.SelectedChoice = list.Choices[0]; // own names
            list.Own.Text = text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
            list.Own.ShowAllErrors();
        }
#pragma warning disable CA1031 // The reason below the button.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LabelsError = ex.Message;
        }
    }

    private bool CanOpen(int index) => index <= Step || (index == Step + 1 && CanNext());

    private void GoTo(int index)
    {
        if (index < Step && !IsWorking)
        {
            Step = index;
            RefreshSteps();
        }
        else if (index == Step + 1)
        {
            NextCommand.Execute(null);
        }
    }

    private void RefreshSteps()
    {
        foreach (var item in Steps)
        {
            item.Refresh();
        }

        OnPropertyChanged(nameof(StepProblem));
    }

    private bool CanNext() => !IsWorking && Step switch
    {
        0 => File is not null && Validation.ErrorOf(nameof(FileName)) is null,
        1 => Validation.ErrorOf(nameof(Name)) is null && Settings is { IsFormValid: true },
        _ => false,
    };

    [RelayCommand(CanExecute = nameof(CanNext))]
    private async Task NextAsync()
    {
        ServerError = null;
        if (Step == 1)
        {
            Validation.ShowAll(nameof(Name));
            if (CanNext())
            {
                Step = 2;
                RefreshSteps();
            }

            return;
        }

        Validation.ShowAll(nameof(FileName));
        if (File is not { } file || !CanNext())
        {
            return;
        }

        if (await CheckAsync(file).ConfigureAwait(true))
        {
            Step = 1;
            RefreshSteps();
        }
    }

    /// <summary>The upload and the check, once per file; then the found model's name, family, settings and labels.</summary>
    private async Task<bool> CheckAsync(IModelFile file)
    {
        if (Stored is not null && ReferenceEquals(_storedFrom, file))
        {
            return true;
        }

        IsWorking = true;
        try
        {
            await ForgetStoredAsync().ConfigureAwait(true);
            if (await SendAsync(file).ConfigureAwait(true) is not { } model)
            {
                return false;
            }

            Stored = model;
            _storedFrom = file;
            Name = model.Name;
            var settings = new ModelSettingsViewModel(model, guided: true);
            settings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ModelSettingsViewModel.IsFormValid) or nameof(ModelSettingsViewModel.FormError))
                {
                    NextCommand.NotifyCanExecuteChanged();
                    OnPropertyChanged(nameof(StepProblem));
                }
            };
            Settings = settings;
            Found = VisionTexts.Found(model);
            _settingFamily = true;
            Families = [.. ModelFamilies.All.Where(f => f.Kind == model.Kind)];
            FoundFamily = ModelFamilies.Of(model);
            Family = FoundFamily;
            _settingFamily = false;
            var classList = ModelEditViewModel.ForClassList(model, _importer.Lists);
            classList.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ModelEditViewModel.Error))
                {
                    ImportCommand.NotifyCanExecuteChanged();
                    OnPropertyChanged(nameof(StepProblem));
                }
            };
            ClassList = classList;
            Validation.Validate();
            return true;
        }
        finally
        {
            IsWorking = false;
            ProgressText = null;
        }
    }

    private async Task<VisionModel?> SendAsync(IModelFile file)
    {
        _sending = new CancellationTokenSource();
        var missing = _importer.Models.Where(m => m.Missing).Select(m => m.Id).ToHashSet(StringComparer.Ordinal); // a Missing model may come back
        Progress = 0;
        ProgressText = VisionTexts.Uploading(0);
        try
        {
            var reply = await _importer.SendAsync(file, null,
                fraction =>
                {
                    Progress = fraction;
                    ProgressText = VisionTexts.Uploading(fraction);
                },
                () => ProgressText = VisionTexts.CheckingModel,
                _sending.Token).ConfigureAwait(true);
            if (reply.Refusal is not null || reply.Model is null)
            {
                FileError = reply.Refusal ?? "This model cannot run here.";
                Validation.ShowAll(nameof(FileName));
                return null;
            }

            _cameBack = missing.Contains(reply.Model.Id);
            return reply.Model;
        }
        catch (OperationCanceledException) when (_sending.IsCancellationRequested)
        {
            return null;
        }
#pragma warning disable CA1031 // The server's reason below the file (over the limits, a refused file).
        catch (Exception ex)
#pragma warning restore CA1031
        {
            FileError = ServerErrors.Message(ex);
            Validation.ShowAll(nameof(FileName));
            return null;
        }
        finally
        {
            _sending.Dispose();
            _sending = null;
        }
    }

    private bool CanBack() => Step > 0 && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanBack))]
    private void Back()
    {
        ServerError = null;
        Step--;
        RefreshSteps();
    }

    private bool CanImport() => Step == 2 && !IsWorking && Settings is { IsFormValid: true } && ClassList is { } list && list.Error is null;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private async Task ImportAsync()
    {
        if (Stored is not { } stored || Settings is not { } settings || ClassList is not { } list)
        {
            return;
        }

        ServerError = null;
        IsWorking = true;
        var model = stored;
        try
        {
            model = settings.ToModel() with { Name = Name.Trim(), ClassListId = list.ClassListId, ClassNames = list.ClassNames };
            Result = await _importer.UpdateAsync(model).ConfigureAwait(true);
            _finished = true;
            CloseRequested?.Invoke(this, true);
        }
#pragma warning disable CA1031 // A refused setting below it on the Settings step, else below the form.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var message = ServerErrors.Message(ex);
            if (settings.ShowRefusal(message))
            {
                Step = 1;
            }
            else
            {
                ServerError = message;
            }

            RefreshSteps();
        }
        finally
        {
            IsWorking = false;
        }
    }

    /// <summary>Cancel: a running upload stops; a model the wizard stored goes again. Closing the window does the same.</summary>
    [RelayCommand]
    private async Task CancelAsync()
    {
        await AbandonAsync().ConfigureAwait(true);
        CloseRequested?.Invoke(this, false);
    }

    /// <summary>The window closed without Import: the upload stops and the stored model goes (once).</summary>
    public async Task AbandonAsync()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _sending?.Cancel();
        await ForgetStoredAsync().ConfigureAwait(true);
    }

    private async Task ForgetStoredAsync()
    {
        var stored = Stored;
        Stored = null;
        Settings = null;
        ClassList = null;
        if (stored is null || _cameBack)
        {
            _cameBack = false;
            return;
        }

        try
        {
            await _importer.DeleteAsync(stored.Id).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The user can delete it in the card.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(FileProblem));
        NextCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(StepProblem));
        foreach (var item in Steps ?? [])
        {
            item.Refresh();
        }
    }

    public void Dispose() => _sending?.Dispose();
}
