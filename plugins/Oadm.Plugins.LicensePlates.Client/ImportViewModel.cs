using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>One row of the import preview: the plate as it will be saved and its status (a refused row says why).</summary>
public sealed record ImportRowViewModel(ImportRow Row)
{
    public int Line => Row.Line;

    public string Plate => Row.Plate;

    public string Name => Row.Name;

    public string Tags => string.Join(", ", Row.Tags);

    public string AllowedFrom => PlateTimes.Text(Row.AllowedFrom);

    public string AllowedTo => PlateTimes.Text(Row.AllowedTo);

    public string StatusText => PageTexts.ImportStatus(Row.Status);

    public string Error => Row.Error ?? string.Empty;

    public bool IsNew => Row.Status == ImportRowStatus.New;

    public bool IsChanged => Row.Status == ImportRowStatus.Changed;

    public bool IsRefused => Row.Status == ImportRowStatus.Refused;
}

/// <summary>
/// The Import dialog (docs/spec/license-plate-center.md "Import and export"): Choose file (ALPV's export or OADM's CSV), the
/// tags every imported plate gets, then the preview the server made (new, changed and refused rows, the row errors in the
/// Status column) before anything is saved; Import saves the new and changed rows in one transaction.
/// </summary>
public sealed partial class ImportViewModel : ObservableObject
{
    private readonly LicensePlatesViewModel _page;
    private string? _uploadId;
    private int _version;

    public ImportViewModel(LicensePlatesViewModel page)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        TagChoices = new CheckChoiceList(PageTexts.AllTags, PageTexts.NoTags);
        TagChoices.Set(page.Tags.All.Select(t => new CheckChoice(t.Id, t.Name)), false, []);
        TagChoices.Changed += (_, _) =>
        {
            if (_uploadId is not null)
            {
                PreviewTask = PreviewAsync();
            }
        };
    }

    public CheckChoiceList TagChoices { get; }

    public RangeObservableCollection<ImportRowViewModel> Rows { get; } = [];

    [ObservableProperty] public partial string FileName { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(CountsText), nameof(ImportBlockedReason), nameof(HasRefused))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand))]
    public partial ImportPreview? Preview { get; private set; }

    /// <summary>The file is sent and read (the loading overlay over the preview).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImportBlockedReason))]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(ChooseFileCommand))]
    public partial bool IsReading { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(ChooseFileCommand))]
    public partial bool IsSaving { get; private set; }

    /// <summary>A refusal of the file (not a CSV, too large) directly below Choose file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileError))]
    public partial string? FileError { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasFileError => !string.IsNullOrEmpty(FileError);

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public bool HasPreview => Preview is not null;

    public bool HasRefused => Preview is { Refused: > 0 };

    /// <summary>"12 new, 3 changed, 1 refused".</summary>
    public string CountsText => Preview is { } p ? PageTexts.ImportCounts(p) : string.Empty;

    /// <summary>Why Import is off (its tooltip); null when it is on.</summary>
    public string? ImportBlockedReason => IsReading ? PageTexts.ReadingFile
        : Preview is null ? PageTexts.ChooseFileFirst
        : Preview.New + Preview.Changed == 0 ? PageTexts.NothingToImport
        : null;

    public ImportResult? Result { get; private set; }

    public event EventHandler<bool>? CloseRequested;

    public Task PreviewTask { get; private set; } = Task.CompletedTask;

    public Task SaveTask { get; private set; } = Task.CompletedTask;

    private bool CanChoose() => !IsReading && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private async Task ChooseFileAsync()
    {
        if (_page.Dialogs is not { } dialogs || await dialogs.OpenCsvAsync().ConfigureAwait(true) is not { } path)
        {
            return;
        }

        await UseFileAsync(path).ConfigureAwait(true);
    }

    /// <summary>Sends a local file and shows its preview (tests call it with a fixture).</summary>
    public async Task UseFileAsync(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FileError = null;
        FileName = Path.GetFileName(path);
        if (new FileInfo(path) is { Exists: true, Length: > LicensePlateLimits.MaxImportBytes })
        {
            FileError = "The file is larger than 16 MB.";
            return;
        }

        IsReading = true;
        try
        {
            var error = await GuardedRun.RunAsync(async () =>
                _uploadId = (await _page.Client.Context.UploadAsync(path, null, CancellationToken.None).ConfigureAwait(true)).Id).ConfigureAwait(true);
            if (error is not null)
            {
                FileError = error;
                return;
            }
        }
        finally
        {
            IsReading = false;
        }

        await (PreviewTask = PreviewAsync()).ConfigureAwait(true);
    }

    private async Task PreviewAsync()
    {
        var version = ++_version;
        if (_uploadId is not { } upload)
        {
            return;
        }

        IsReading = true;
        ErrorText = null;
        try
        {
            var error = await GuardedRun.RunAsync(async () =>
            {
                var tags = TagChoices.Items.Where(c => c.Chosen).Select(c => (Guid)c.Value!).ToList();
                var preview = await _page.Client.CallAsync<ImportPreview>(LicensePlatesMethods.ImportPreview, new ImportRequest(upload, tags)).ConfigureAwait(true);
                if (version != _version)
                {
                    return;
                }

                if (preview.Error is { Length: > 0 } fileError)
                {
                    FileError = fileError;
                    Preview = null;
                    Rows.ReplaceAll([]);
                    return;
                }

                var rows = new List<ImportRowViewModel>();
                int? after = 0;
                while (after is { } start && version == _version)
                {
                    var page = await _page.Client.CallAsync<ImportRowsPage>(LicensePlatesMethods.ImportRows, new ImportRowsRequest(preview.PreviewId, start)).ConfigureAwait(true);
                    rows.AddRange(page.Rows.Select(r => new ImportRowViewModel(r)));
                    after = page.Next;
                }

                if (version == _version)
                {
                    // Refused rows first: they are the ones to look at.
                    Rows.ReplaceAll([.. rows.OrderBy(r => r.IsRefused ? 0 : 1).ThenBy(r => r.Line)]);
                    Preview = preview;
                }
            }).ConfigureAwait(true);
            if (error is not null && version == _version)
            {
                ErrorText = error;
            }
        }
        finally
        {
            if (version == _version)
            {
                IsReading = false;
            }
        }
    }

    private bool CanImport() => !IsReading && !IsSaving && ImportBlockedReason is null;

    [RelayCommand(CanExecute = nameof(CanImport))]
    private Task ImportAsync() => SaveTask = SaveAsync();

    private async Task SaveAsync()
    {
        if (Preview is not { } preview)
        {
            return;
        }

        IsSaving = true;
        ErrorText = null;
        try
        {
            ErrorText = await GuardedRun.RunAsync(async () =>
            {
                Result = await _page.Client.CallAsync<ImportResult>(LicensePlatesMethods.ImportSave, new ImportSaveRequest(preview.PreviewId)).ConfigureAwait(true);
                CloseRequested?.Invoke(this, true);
            }).ConfigureAwait(true);
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
