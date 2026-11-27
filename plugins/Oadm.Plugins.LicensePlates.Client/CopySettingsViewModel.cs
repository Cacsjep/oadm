using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Validation;

namespace Oadm.Plugins.LicensePlates.Client;

/// <summary>
/// Copy settings (docs/spec/license-plate-center.md "Set up"): Recognition and the event switches of one camera to the chosen
/// cameras of the plugin, one task Set up each; the areas only when asked, since they belong to the picture of one camera.
/// </summary>
public sealed partial class CopySettingsViewModel : ValidatingViewModel
{
    private readonly LicensePlatesViewModel _page;

    public CopySettingsViewModel(LicensePlatesViewModel page, CameraRowViewModel source)
    {
        _page = page ?? throw new ArgumentNullException(nameof(page));
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Validation.Rule(nameof(TargetIds), () => TargetIds.Count == 0 ? PageTexts.ChooseTargetsFirst : null);
        Validation.Validate();
    }

    public CameraRowViewModel Source { get; }

    public string SourceText => Source.Camera;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetsText))]
    public partial IReadOnlyList<Guid> TargetIds { get; private set; } = [];

    /// <summary>"Choose cameras", "3 cameras".</summary>
    public string TargetsText => TargetIds.Count == 0 ? PageTexts.ChooseCameras : PageTexts.CameraCount(TargetIds.Count);

    [ObservableProperty] public partial bool CopyAreas { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    public partial bool IsSaving { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public string? CopyBlockedReason => IsFormValid ? null : FormError;

    /// <summary>The visible error below To.</summary>
    public string? TargetsError => ErrorOf(nameof(TargetIds));

    public event EventHandler<bool>? CloseRequested;

    public Task SaveTask { get; private set; } = Task.CompletedTask;

    /// <summary>The cameras to copy to (tests).</summary>
    public void SetTargets(IEnumerable<Guid> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        TargetIds = [.. ids.Where(id => id != Source.DeviceId).Distinct()];
        Validation.Touch(nameof(TargetIds));
        Validation.Validate();
    }

    [RelayCommand]
    private async Task ChooseTargetsAsync()
    {
        if (_page.Dialogs is not { } dialogs)
        {
            return;
        }

        var devices = _page.Client.Context.Devices.Where(d => d.Id != Source.DeviceId && _page.Cameras.Contains(d.Id)).ToList();
        if (await dialogs.PickCamerasAsync(devices, TargetIds, PageTexts.CopyTo).ConfigureAwait(true) is { } ids)
        {
            SetTargets(ids);
        }
    }

    private bool CanCopy() => IsFormValid && !IsSaving;

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private Task CopyAsync() => SaveTask = CopyCoreAsync();

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    protected override void OnValidationChanged()
    {
        CopyCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CopyBlockedReason));
        OnPropertyChanged(nameof(TargetsError));
    }

    private async Task CopyCoreAsync()
    {
        IsSaving = true;
        ErrorText = null;
        try
        {
            ErrorText = await GuardedRun.RunAsync(async () =>
            {
                await _page.Client.CallAsync<TasksReply>(LicensePlatesMethods.CopySettings, new CopySettingsRequest(Source.DeviceId, TargetIds, CopyAreas)).ConfigureAwait(true);
                CloseRequested?.Invoke(this, true);
            }).ConfigureAwait(true);
        }
        finally
        {
            IsSaving = false;
        }
    }
}
