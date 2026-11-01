using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Vulnerabilities;

namespace Oadm.Client.Settings;

/// <summary>
/// The Vulnerabilities card (docs/spec/vulnerabilities.md): who updates the list of Axis CVEs online (a server setting, saved
/// with the others), the list's date and where it came from, Check now.
/// </summary>
public sealed partial class SettingsViewModel
{
    /// <summary>The choices of "Update the list online", in the order of the box.</summary>
    public static IReadOnlyList<string> VulnerabilityUpdateChoices { get; } = ["Off", "By the server", "By the clients"];

    private static readonly string[] VulnerabilityUpdateValues = ["off", "server", "clients"];

    private VulnerabilityCatalog? _vulnerabilities;
    private VulnerabilityUpdates? _vulnerabilityUpdates;

    /// <summary>"Update the list online": 0 Off, 1 By the server, 2 By the clients.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VulnerabilityUpdateText))]
    public partial int VulnerabilityUpdateIndex { get; set; }

    /// <summary>The choice as text (operators see it read only).</summary>
    public string VulnerabilityUpdateText => VulnerabilityUpdateChoices[Math.Clamp(VulnerabilityUpdateIndex, 0, 2)];

    /// <summary>"60 CVEs, list of 1 Nov 2026 (OADM release)".</summary>
    public string VulnerabilityListText
    {
        get
        {
            if (_vulnerabilities?.Updated is not { } updated)
            {
                return "Not loaded yet";
            }

            var from = _vulnerabilities.Source switch
            {
                "server" => "fetched by the server",
                "client" => "fetched by a client",
                _ => "OADM release",
            };
            return string.Create(CultureInfo.CurrentCulture, $"{_vulnerabilities.Count} CVEs, list of {updated.ToLocalTime():d} ({from})");
        }
    }

    /// <summary>Check now works while the saved setting fetches online.</summary>
    public bool CanCheckVulnerabilities => _vulnerabilities?.Update is "server" or "clients" && !IsCheckingVulnerabilities;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckVulnerabilities))]
    [NotifyCanExecuteChangedFor(nameof(CheckVulnerabilitiesCommand))]
    public partial bool IsCheckingVulnerabilities { get; private set; }

    /// <summary>The result of Check now ("The list is up to date.", or why it failed).</summary>
    [ObservableProperty] public partial string? VulnerabilityMessage { get; private set; }

    [ObservableProperty] public partial bool VulnerabilityMessageIsError { get; private set; }

    /// <summary>Connects the card to the client's list (the device store's catalog).</summary>
    internal void AttachVulnerabilities(VulnerabilityCatalog catalog, VulnerabilityUpdates updates)
    {
        _vulnerabilities = catalog;
        _vulnerabilityUpdates = updates;
        catalog.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(VulnerabilityListText));
            OnPropertyChanged(nameof(CanCheckVulnerabilities));
            CheckVulnerabilitiesCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>The server's list and setting again (after Save); a failure leaves the card as it was.</summary>
    private async Task ReadVulnerabilitiesAsync()
    {
        if (_vulnerabilityUpdates is null)
        {
            return;
        }

        try
        {
            await _vulnerabilityUpdates.ReadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (RpcException)
        {
            // an older server without the service: the card stays as it was
        }
    }

    private static int VulnerabilityIndexOf(string? value) => Math.Max(0, Array.IndexOf(VulnerabilityUpdateValues, value ?? "off"));

    private string VulnerabilityValue => VulnerabilityUpdateValues[Math.Clamp(VulnerabilityUpdateIndex, 0, 2)];

    [RelayCommand(CanExecute = nameof(CanCheckVulnerabilities))]
    private async Task CheckVulnerabilitiesAsync()
    {
        if (_vulnerabilities is null || _vulnerabilityUpdates is null)
        {
            return;
        }

        IsCheckingVulnerabilities = true;
        VulnerabilityMessage = null;
        try
        {
            if (_vulnerabilities.Update == "server")
            {
                await _vulnerabilityUpdates.RefreshOnServerAsync(CancellationToken.None).ConfigureAwait(true);
            }
            else
            {
                await _vulnerabilityUpdates.SyncAsync(force: true, CancellationToken.None).ConfigureAwait(true);
            }

            VulnerabilityMessageIsError = false;
            VulnerabilityMessage = "Checked. The list is up to date.";
        }
        catch (RpcException ex)
        {
            VulnerabilityMessageIsError = true;
            VulnerabilityMessage = ex.Status.Detail;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException)
        {
            VulnerabilityMessageIsError = true;
            VulnerabilityMessage = "The vulnerability list could not be fetched. Check this computer's internet access.";
        }
        finally
        {
            IsCheckingVulnerabilities = false;
        }
    }
}
