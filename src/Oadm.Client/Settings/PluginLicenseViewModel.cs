using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Client.Dialogs;
using Oadm.Client.Plugins;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Licensing;

namespace Oadm.Client.Settings;

/// <summary>
/// "Enter license" of the Plugins page (administrators): the license text pasted or read from a file, checked and stored
/// by the server (PluginService.SetPackageLicense); its refusal ("for another server", "expired", "signature does not
/// match") stands below the field. The server ID is shown with a copy button, so the customer can send it to the vendor.
/// </summary>
public sealed partial class PluginLicenseViewModel : ValidatingViewModel
{
    private static readonly FileType LicenseFileType = new("License file", LicenseFile.Extension.TrimStart('.'), "application/json");

    private readonly PluginPackageStore _store;
    private readonly IDialogService? _dialogs;
    private readonly IClipboardService? _clipboard;

    public PluginLicenseViewModel(PluginPackageStore store, string packageId, string pluginName, IDialogService? dialogs = null, IClipboardService? clipboard = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        PackageId = packageId;
        PluginName = pluginName;
        _dialogs = dialogs;
        _clipboard = clipboard;
        Validation.Rule(nameof(LicenseText), () => string.IsNullOrWhiteSpace(LicenseText) ? "Paste the license or choose its file." : null);
        Validation.Validate();
    }

    public string PackageId { get; }

    public string PluginName { get; }

    public string Title => "License of " + PluginName;

    public string ServerId => _store.ServerId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string LicenseText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;

    public string? SaveBlockedReason => Validation.FirstErrorOf([nameof(LicenseText)]);

    /// <summary>The error below the license field (the code editor has no validation border of its own).</summary>
    public string? LicenseError => ErrorOf(nameof(LicenseText));

    /// <summary>True: stored (the dialog closes); false: cancelled.</summary>
    public event EventHandler<bool>? CloseRequested;

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(SaveBlockedReason));
        OnPropertyChanged(nameof(LicenseError));
        SaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        if (_dialogs is null)
        {
            return;
        }

        var file = await _dialogs.OpenFileAsync("Choose the license file", LicenseFileType, LicenseFile.MaxLength).ConfigureAwait(true);
        if (file is null)
        {
            return;
        }

        if (file.IsTooLarge)
        {
            Validation.SetServerError(nameof(LicenseText), "This file is too large for a license.");
            return;
        }

        LicenseText = Encoding.UTF8.GetString(file.Content).TrimStart('﻿');
    }

    [RelayCommand]
    private async Task CopyServerIdAsync()
    {
        if (_clipboard is not null && ServerId.Length > 0)
        {
            await _clipboard.SetTextAsync(ServerId).ConfigureAwait(true);
        }
    }

    private bool CanSave() => !IsBusy && Validation.IsValid;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Validation.ShowAll();
        if (Validation.HasErrors)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _store.SetLicenseAsync(PackageId, LicenseText.Trim(), CancellationToken.None).ConfigureAwait(true);
            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Validation.SetServerError(nameof(LicenseText), ex is Grpc.Core.RpcException rpc && rpc.Status.Detail.Length > 0 ? rpc.Status.Detail : ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);
}
