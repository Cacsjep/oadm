using System.Globalization;
using System.Text.RegularExpressions;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Est.Client;

/// <summary>A choice of a combo box: the stored value and its label.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One profile of the form: name, use, validity, key, on; errors below its fields.</summary>
public sealed partial class ProfileRowViewModel : ValidatingViewModel
{
    private readonly EstViewModel _owner;

    internal ProfileRowViewModel(EstViewModel owner, EstProfile profile)
    {
        _owner = owner;
        Name = profile.Name;
        Use = EstViewModel.Uses.First(u => u.Value == profile.Use);
        Validity = profile.ValidityDays.ToString(CultureInfo.InvariantCulture);
        Key = EstViewModel.Keys.First(k => k.Value == profile.Key);
        Enabled = profile.Enabled;
        Validation
            .Rule(nameof(Name), () => !NamePattern().IsMatch(Name.Trim()) ? "Use 1 to 32 letters, digits, - or _."
                : _owner.Profiles.Any(p => p != this && string.Equals(p.Name.Trim(), Name.Trim(), StringComparison.OrdinalIgnoreCase)) ? "Another profile has this name."
                : null)
            .Rule(nameof(Validity), () => int.TryParse(Validity, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 825
                ? null : "Use 1 to 825 days.")
            .Rule(nameof(Use), () => _owner.Profiles.Any(p => p != this && p.Use == Use) ? "Another profile has this use." : null);
        Validation.Validate();
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial Choice<ProfileUse> Use { get; set; }

    [ObservableProperty]
    public partial string Validity { get; set; }

    [ObservableProperty]
    public partial Choice<ProfileKey> Key { get; set; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

#pragma warning disable CA1822 // Instance properties for the compiled bindings of the row template.
    public IReadOnlyList<Choice<ProfileUse>> UseChoices => EstViewModel.Uses;

    public IReadOnlyList<Choice<ProfileKey>> KeyChoices => EstViewModel.Keys;
#pragma warning restore CA1822

    public bool MayManage => _owner.MayManage;

    internal EstProfile ToProfile() => new(
        Name.Trim(),
        Use.Value,
        int.TryParse(Validity, NumberStyles.None, CultureInfo.InvariantCulture, out var days) ? days : 0,
        Key.Value,
        Enabled);

    internal void Revalidate() => Validation.Validate();

    internal void ShowServerError(string field, string error) => Validation.SetServerError(field, error);

    internal void ShowAllErrors() => Validation.ShowAll();

    internal void NotifyRole() => OnPropertyChanged(nameof(MayManage));

    protected override void OnValidationChanged() => _owner.ProfileValidationChanged();

    partial void OnNameChanged(string value) => _owner.ProfilesEdited();

    partial void OnUseChanged(Choice<ProfileUse> value) => _owner.ProfilesEdited();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex NamePattern();
}

/// <summary>An enrollment as the list shows it.</summary>
public sealed record EnrollmentRow(Enrollment Source)
{
    public string Time => Source.TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    public DateTime TimeSortKey => Source.TimeUtc;

    public string Device => Source.Device;

    public string Profile => Source.Profile;

    public string Kind => Source.Kind == EnrollmentKind.Renewal ? "Renewal" : "New";

    public string Result => Source.Ok ? "Enrolled" : "Refused";

    public bool Ok => Source.Ok;

    public bool Refused => !Source.Ok;

    public string Reason => Source.Reason;
}

/// <summary>A certificate EST issued as the list shows it.</summary>
public sealed record CertificateRow(IssuedCertificate Source)
{
    public string Device => Source.Device;

    public string Profile => Source.Profile;

    public string Issued => Source.IssuedUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);

    public DateTime IssuedSortKey => Source.IssuedUtc;

    public string ValidUntil => Source.NotAfterUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);

    public DateTime ValidUntilSortKey => Source.NotAfterUtc;

    public string State => Source.Revoked ? "Revoked" : Source.NotAfterUtc < DateTime.UtcNow ? "Expired" : "Valid";

    public bool IsValid => State == "Valid";

    public bool IsProblem => !IsValid;

    public bool CanRevoke => !Source.Revoked;
}

/// <summary>
/// The EST server page (docs/spec/est-server.md): port, profiles and Save with the status left of it; the newest
/// enrollments; the certificates EST issued with Revoke. <see cref="Activate"/> reads the state and watches the live
/// events while the page is shown.
/// </summary>
public sealed partial class EstViewModel : ValidatingViewModel, IDisposable
{
    public static IReadOnlyList<Choice<ProfileUse>> Uses { get; } =
    [
        new(ProfileUse.Https, "HTTPS"),
        new(ProfileUse.Dot1x, "IEEE 802.1X"),
        new(ProfileUse.Mqtt, "MQTT"),
        new(ProfileUse.Rtsps, "RTSPS"),
    ];

    public static IReadOnlyList<Choice<ProfileKey>> Keys { get; } =
    [
        new(ProfileKey.Ecc, "ECC"),
        new(ProfileKey.Rsa, "RSA"),
    ];

    private readonly ICorePluginClientContext _ctx;
    private CancellationTokenSource? _active;
    private bool _profilesValid = true;

    public EstViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _ctx.PermissionsChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(MayManage));
            foreach (var row in Profiles)
            {
                row.NotifyRole();
            }

            SaveCommand.NotifyCanExecuteChanged();
            AddProfileCommand.NotifyCanExecuteChanged();
        };
        Validation
            .Rule(nameof(Port), () => int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1025 and <= 65535
                ? null : "Use 1025 to 65535.")
            .Rule(nameof(EnrollmentsKept), () => int.TryParse(EnrollmentsKept, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var kept) && kept is >= 100 and <= 5000
                ? null : "Keep from 100 to 5,000 enrollments.");
        Validation.Validate();
    }

    [ObservableProperty]
    public partial string Port { get; set; } = EstPluginInfo.DefaultPort.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial string EnrollmentsKept { get; set; } = EstPluginInfo.DefaultEnrollmentsKept.ToString(CultureInfo.InvariantCulture);

    public RangeObservableCollection<ProfileRowViewModel> Profiles { get; } = [];

    public RangeObservableCollection<EnrollmentRow> Enrollments { get; } = [];

    public RangeObservableCollection<CertificateRow> Certificates { get; } = [];

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Loading";

    [ObservableProperty]
    public partial bool StatusOk { get; set; }

    /// <summary>Where it listens: "This server: listening on TCP 9454".</summary>
    [ObservableProperty]
    public partial string ListenersText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CertificateSearch { get; set; } = string.Empty;

    /// <summary>"1,204 certificates" or "Showing 500 of 1,204 certificates".</summary>
    [ObservableProperty]
    public partial string CertificateSummary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; set; }

    /// <summary>A save or revoke failed: the reason, below the Save button's row.</summary>
    [ObservableProperty]
    public partial string? ActionError { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    public bool MayManage => _ctx.Has(PermissionKeys.Of(EstPluginInfo.PluginId, PermissionKeys.Manage));

    public bool HasEnrollments => Enrollments.Count > 0;

    public bool HasCertificates => Certificates.Count > 0;

    public bool CanAddProfile => MayManage && Profiles.Count < Uses.Count;

    /// <summary>Why Save is off, for its tooltip.</summary>
    public string? SaveTip => !IsFormValid ? FormError : Profiles.Select(p => p.FormError).FirstOrDefault(e => e is not null);

    public void Activate()
    {
        Deactivate();
        _active = new CancellationTokenSource();
        var ct = _active.Token;
        IsLoading = true;
        _ = LoadAsync(ct);
        _ = _ctx.RunAsync(OnEvent, null, ct);
    }

    public void Deactivate()
    {
        _active?.Cancel();
        _active?.Dispose();
        _active = null;
    }

    public void Dispose() => Deactivate();

    internal async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            ApplyState(EstJson.Deserialize<EstState>(await _ctx.InvokeAsync(EstMethods.GetState, null, ct).ConfigureAwait(true)), form: true);
            var enrollments = EstJson.Deserialize<EnrollmentList>(await _ctx.InvokeAsync(EstMethods.GetEnrollments, null, ct).ConfigureAwait(true));
            Enrollments.ReplaceAll(enrollments.Enrollments.Select(e => new EnrollmentRow(e)));
            OnPropertyChanged(nameof(HasEnrollments));
            await LoadCertificatesAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // The page shows the reason instead of crashing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusText = ex.Message;
            StatusOk = false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal async Task LoadCertificatesAsync(CancellationToken ct)
    {
        var list = EstJson.Deserialize<CertificateList>(await _ctx.InvokeAsync(
            EstMethods.GetCertificates, EstJson.Serialize(new CertificatesRequest(CertificateSearch)), ct).ConfigureAwait(true));
        Certificates.ReplaceAll(list.Certificates.Select(c => new CertificateRow(c)));
        CertificateSummary = list.Total == 0 ? string.Empty
            : list.Total > list.Certificates.Count
                ? string.Create(CultureInfo.CurrentCulture, $"Showing {list.Certificates.Count:N0} of {list.Total:N0} certificates")
                : list.Total == 1 ? "1 certificate" : string.Create(CultureInfo.CurrentCulture, $"{list.Total:N0} certificates");
        OnPropertyChanged(nameof(HasCertificates));
    }

    [RelayCommand(CanExecute = nameof(CanAddProfile))]
    private void AddProfile()
    {
        var use = Uses.First(u => Profiles.All(p => p.Use.Value != u.Value)).Value;
        Profiles.Add(new ProfileRowViewModel(this, new EstProfile(use.ToString().ToLowerInvariant(), use)));
        ProfilesEdited();
    }

    [RelayCommand]
    private void RemoveProfile(ProfileRowViewModel? row)
    {
        if (row is not null && MayManage && Profiles.Count > 1)
        {
            Profiles.Remove(row);
            ProfilesEdited();
        }
    }

    private bool CanSave() => MayManage && !IsSaving && IsFormValid && _profilesValid;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsSaving = true;
        ActionError = null;
        try
        {
            var config = new EstConfig
            {
                Port = int.Parse(Port, NumberStyles.None, CultureInfo.InvariantCulture),
                EnrollmentsKept = int.Parse(EnrollmentsKept, NumberStyles.AllowThousands, CultureInfo.InvariantCulture),
                Profiles = [.. Profiles.Select(p => p.ToProfile())],
            };
            var reply = EstJson.Deserialize<SaveReply>(await _ctx.InvokeAsync(EstMethods.Save, EstJson.Serialize(new SaveRequest(config)), CancellationToken.None).ConfigureAwait(true));
            if (reply.Error is { } error)
            {
                ShowServerError(reply.Field ?? string.Empty, error);
            }
            else if (reply.State is { } state)
            {
                ApplyState(state, form: true);
            }
        }
#pragma warning disable CA1031 // The page shows the reason.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ActionError = ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private async Task RevokeAsync(CertificateRow? row)
    {
        if (row is null || !row.CanRevoke || !MayManage)
        {
            return;
        }

        var message = $"Revoke the {row.Profile} certificate of {row.Device}? The camera keeps it until it expires but gets no new one for this profile until you set it up again.";
        if (!await _ctx.ConfirmAsync("Revoke certificate", message, "Revoke").ConfigureAwait(true))
        {
            return;
        }

        ActionError = null;
        try
        {
            await _ctx.InvokeAsync(EstMethods.Revoke, EstJson.Serialize(new RevokeRequest([row.Source.SerialNumber])), CancellationToken.None).ConfigureAwait(true);
            await LoadCertificatesAsync(CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // The page shows the reason.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ActionError = ex.Message;
        }
    }

    internal void ProfilesEdited()
    {
        foreach (var row in Profiles)
        {
            row.Revalidate();
        }

        AddProfileCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddProfile));
        ProfileValidationChanged();
    }

    internal void ProfileValidationChanged()
    {
        _profilesValid = Profiles.All(p => p.IsFormValid);
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SaveTip));
    }

    protected override void OnValidationChanged()
    {
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SaveTip));
    }

    partial void OnCertificateSearchChanged(string value) => _ = SearchAsync();

    private async Task SearchAsync()
    {
        try
        {
            await LoadCertificatesAsync(_active?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A failed search leaves the list as it was.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void ShowServerError(string field, string error)
    {
        var match = ProfileField().Match(field);
        if (match.Success && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) is var index && index < Profiles.Count)
        {
            var property = match.Groups[2].Value switch
            {
                "use" => nameof(ProfileRowViewModel.Use),
                "validityDays" => nameof(ProfileRowViewModel.Validity),
                _ => nameof(ProfileRowViewModel.Name),
            };
            Profiles[index].ShowServerError(property, error);
            return;
        }

        switch (field)
        {
            case "port":
                Validation.SetServerError(nameof(Port), error);
                break;
            case "enrollmentsKept":
                Validation.SetServerError(nameof(EnrollmentsKept), error);
                break;
            default:
                ActionError = error;
                break;
        }
    }

    private void ApplyState(EstState state, bool form)
    {
        StatusText = state.StatusText;
        StatusOk = state.StatusOk;
        ListenersText = string.Join(Environment.NewLine, state.Listeners.Where(l => !l.Ok).Select(l => l.Text));
        if (!form)
        {
            return;
        }

        Port = state.Config.Port.ToString(CultureInfo.InvariantCulture);
        EnrollmentsKept = state.Config.EnrollmentsKept.ToString(CultureInfo.InvariantCulture);
        Profiles.ReplaceAll(state.Config.Profiles.Select(p => new ProfileRowViewModel(this, p)));
        Validation.Reset();
        ProfilesEdited();
    }

    private void OnEvent(PluginEvent e)
    {
        switch (e.Topic)
        {
            case EstMethods.StateTopic:
                ApplyState(EstJson.Deserialize<EstState>(e.PayloadJson), form: false);
                break;
            case EstMethods.EnrollmentsTopic:
                var added = EstJson.Deserialize<EnrollmentList>(e.PayloadJson).Enrollments.Select(x => new EnrollmentRow(x)).ToList();
                Enrollments.InsertRange(0, added);
                if (int.TryParse(EnrollmentsKept, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var kept) && Enrollments.Count > kept)
                {
                    Enrollments.ReplaceAll([.. Enrollments.Take(kept)]);
                }
                OnPropertyChanged(nameof(HasEnrollments));
                if (added.Any(a => a.Ok))
                {
                    _ = SearchAsync();
                }

                break;
        }
    }

    [GeneratedRegex(@"^profiles\[(\d+)\]\.(\w+)$")]
    private static partial Regex ProfileField();
}
