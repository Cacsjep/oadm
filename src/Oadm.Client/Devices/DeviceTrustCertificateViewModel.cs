using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Client.Devices;

/// <summary>One device of the "Trust new certificate" dialog: the certificate it presents now, or why there is none.</summary>
public sealed partial class TrustCertificateRowViewModel(DeviceRowViewModel device) : ObservableObject
{
    public DeviceRowViewModel Device { get; } = device;

    public string Id => Device.Id;

    public string DeviceText => string.IsNullOrEmpty(Device.Model) ? Device.DisplayAddress : $"{Device.DisplayAddress} ({Device.Model})";

    /// <summary>The fingerprint shown to the user and sent back on Trust; empty when there is nothing to trust.</summary>
    [ObservableProperty] public partial string Fingerprint { get; private set; } = "";

    /// <summary>"3F:9A:61:..." (SHA-256, pairs).</summary>
    public string FingerprintText => DeviceTrustCertificateViewModel.Pairs(Fingerprint);

    [ObservableProperty] public partial string IssuedTo { get; private set; } = "";

    [ObservableProperty] public partial string IssuedBy { get; private set; } = "";

    [ObservableProperty] public partial string ValidUntil { get; private set; } = "";

    /// <summary>Reading, the reason there is nothing to trust, or why trusting failed; empty while it can be trusted.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusText { get; set; } = DeviceTrustCertificateViewModel.ReadingText;

    public bool HasStatus => StatusText.Length > 0;

    /// <summary>The device presents a certificate the user can trust.</summary>
    public bool CanTrust => Fingerprint.Length > 0;

    internal void Apply(PresentedCertificate presented)
    {
        Fingerprint = presented.Message.Length == 0 ? presented.Fingerprint : "";
        OnPropertyChanged(nameof(FingerprintText));
        OnPropertyChanged(nameof(CanTrust));
        IssuedTo = presented.Subject;
        IssuedBy = presented.Issuer;
        ValidUntil = presented.NotAfterUnixMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(presented.NotAfterUnixMs).ToLocalTime().ToString("d", CultureInfo.CurrentCulture)
            : "";
        StatusText = presented.Message;
    }

    internal void Fail(string message)
    {
        Fingerprint = "";
        OnPropertyChanged(nameof(FingerprintText));
        OnPropertyChanged(nameof(CanTrust));
        StatusText = message;
    }
}

/// <summary>
/// "Trust new certificate" dialog (device context menu) for devices with status Certificate changed, instead of removing
/// and adding them again: reads the certificate each device presents now (in pages, nothing is sent to the devices), shows
/// it, and Trust pins exactly the shown fingerprints. The devices that were trusted leave the list; the others keep their
/// reason in the Status column.
/// </summary>
public sealed partial class DeviceTrustCertificateViewModel : ObservableObject
{
    public const string ReadingText = "Reading the certificate";

    /// <summary>Devices per server call (reading and trusting).</summary>
    public const int PageSize = 200;

    private readonly IOadmApi _api;
    private readonly Dictionary<string, TrustCertificateRowViewModel> _byId;

    public DeviceTrustCertificateViewModel(IOadmApi api, IReadOnlyList<DeviceRowViewModel> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _api = api ?? throw new ArgumentNullException(nameof(api));
        Rows.AddRange(devices.Select(d => new TrustCertificateRowViewModel(d)));
        _byId = Rows.ToDictionary(r => r.Id);
    }

    public RangeObservableCollection<TrustCertificateRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TrustCommand))]
    [NotifyPropertyChangedFor(nameof(TrustTip))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty] public partial string BusyText { get; private set; } = "";

    /// <summary>A whole-table error (the server could not be asked), directly below the table.</summary>
    [ObservableProperty] public partial string? ErrorText { get; private set; }

    /// <summary>Devices whose certificate was trusted (all calls of this dialog).</summary>
    public int TrustedCount { get; private set; }

    /// <summary>Why Trust is disabled; null when it is not.</summary>
    public string? TrustTip => IsBusy ? null : Rows.Any(r => r.CanTrust) ? null : "No device presents a certificate to trust";

    /// <summary>True: every device was trusted (the dialog closes); false: cancelled.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Reads the presented certificates, one page of devices per call.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        IsBusy = true;
        ErrorText = null;
        try
        {
            List<string> ids = [.. Rows.Select(r => r.Id)];
            for (int i = 0; i < ids.Count; i += PageSize)
            {
                BusyText = Rows.Count == 1
                    ? ReadingText
                    : string.Create(CultureInfo.CurrentCulture, $"Reading the certificates ({Math.Min(i + PageSize, ids.Count):N0} of {ids.Count:N0})");
                IReadOnlyList<PresentedCertificate> page = await _api.GetPresentedCertificatesAsync(ids.GetRange(i, Math.Min(PageSize, ids.Count - i)), ct).ConfigureAwait(true);
                foreach (PresentedCertificate presented in page)
                {
                    if (_byId.TryGetValue(presented.DeviceId, out TrustCertificateRowViewModel? row))
                    {
                        row.Apply(presented);
                    }
                }
            }
        }
        catch (RpcException ex)
        {
            ErrorText = "The certificates could not be read: " + ex.Status.Detail;
            foreach (TrustCertificateRowViewModel row in Rows.Where(r => r.StatusText == ReadingText))
            {
                row.Fail("");
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(TrustTip));
        }
    }

    [RelayCommand(CanExecute = nameof(CanTrust))]
    private async Task TrustAsync()
    {
        List<TrustCertificateRowViewModel> targets = [.. Rows.Where(r => r.CanTrust)];
        if (targets.Count == 0)
        {
            return;
        }

        IsBusy = true;
        ErrorText = null;
        BusyText = targets.Count == 1
            ? "Trusting the certificate of " + targets[0].Device.DisplayAddress
            : string.Create(CultureInfo.CurrentCulture, $"Trusting the certificates of {targets.Count:N0} devices");
        var trusted = new HashSet<string>();
        try
        {
            for (int i = 0; i < targets.Count; i += PageSize)
            {
                Dictionary<string, string> page = targets.Skip(i).Take(PageSize).ToDictionary(r => r.Id, r => r.Fingerprint);
                TrustNewCertificatesReply reply = await _api.TrustNewCertificatesAsync(page, CancellationToken.None).ConfigureAwait(true);
                foreach (TrustNewCertificateResult result in reply.Results)
                {
                    if (result.Ok)
                    {
                        trusted.Add(result.DeviceId);
                    }
                    else if (_byId.TryGetValue(result.DeviceId, out TrustCertificateRowViewModel? row))
                    {
                        row.Fail(result.Message);
                    }
                }
            }
        }
        catch (RpcException ex)
        {
            ErrorText = "The certificates could not be trusted: " + ex.Status.Detail;
        }
        finally
        {
            TrustedCount += trusted.Count;
            if (trusted.Count > 0)
            {
                Rows.RemoveAll(r => trusted.Contains(r.Id));
                foreach (string id in trusted)
                {
                    _byId.Remove(id);
                }
            }

            IsBusy = false;
            OnPropertyChanged(nameof(TrustTip));
        }

        if (Rows.Count == 0)
        {
            CloseRequested?.Invoke(this, true);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private bool CanTrust() => !IsBusy && Rows.Any(r => r.CanTrust);

    /// <summary>"3F9A61" as "3F:9A:61".</summary>
    internal static string Pairs(string hex) =>
        hex.Length < 2 ? hex : string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
}
