using System.Globalization;
using System.Text.RegularExpressions;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Pki.Client.Servers;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Client.Collections;
using Oadm.Sdk.Client.Network;
using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Network;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Est.Client;

/// <summary>A choice of a combo box: the stored value and its label.</summary>
public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One profile of the form: name, use, issuing policy, on; errors below its fields.</summary>
public sealed partial class ProfileRowViewModel : ValidatingViewModel
{
    private readonly EstViewModel _owner;
    private readonly EstProfile _source; // the stored profile: the validity and key of OADM 4.1 stay in it (no longer read)

    internal ProfileRowViewModel(EstViewModel owner, EstProfile profile)
    {
        _owner = owner;
        _source = profile;
        Name = profile.Name;
        Use = EstViewModel.Uses.First(u => u.Value == profile.Use);
        PolicyChoices = owner.PolicyChoicesFor(Use.Value);
        Policy = PolicyChoices.FirstOrDefault(c => c.Value == profile.PolicyId) ?? (profile.PolicyId is null && PolicyChoices.Count > 0 ? PolicyChoices[0] : null);
        Enabled = profile.Enabled;
        Validation
            .Rule(nameof(Name), () => !NamePattern().IsMatch(Name.Trim()) ? "Use 1 to 32 letters, digits or _."
                : _owner.Profiles.Any(p => p != this && string.Equals(p.Name.Trim(), Name.Trim(), StringComparison.OrdinalIgnoreCase)) ? "Another profile has this name."
                : null)

            // Without PKI's policies (none listed) the server takes the profile's own values: nothing to choose.
            .Rule(nameof(Policy), () => Policy is null && _owner.IssuingPolicies.Count > 0 ? EstTexts.ChoosePolicy : null)
            .Rule(nameof(Use), () => _owner.Profiles.Any(p => p != this && p.Use == Use) ? "Another profile has this use." : null);
        Validation.Validate();
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial Choice<ProfileUse> Use { get; set; }

    /// <summary>The issuing policies of the use's kind (HTTPS certificate for HTTPS and RTSPS, IEEE 802.1X certificate for IEEE 802.1X and MQTT).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Choice<Guid>> PolicyChoices { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolicySummary))]
    public partial Choice<Guid>? Policy { get; set; }

    /// <summary>"OADM Root CA SERVER01, 365 days, ECC" of the chosen policy (its tooltip).</summary>
    public string? PolicySummary => Policy is { } chosen ? _owner.IssuingPolicies.FirstOrDefault(p => p.Id == chosen.Value)?.Summary : null;

    [ObservableProperty]
    public partial bool Enabled { get; set; }

#pragma warning disable CA1822 // Instance properties for the compiled bindings of the row template.
    public IReadOnlyList<Choice<ProfileUse>> UseChoices => EstViewModel.Uses;
#pragma warning restore CA1822

    public bool MayManage => _owner.MayManage;

    /// <summary>
    /// The profile to save (decision 2026-10-25: no On column; a new profile is on, remove a profile instead). Its stored
    /// on/off stays as it was: a profile that was off in OADM 4.1 is never turned on by a save of the page.
    /// </summary>
    internal EstProfile ToProfile() => _source with { Name = Name.Trim(), Use = Use.Value, PolicyId = Policy?.Value ?? _source.PolicyId };

    /// <summary>PKI's policies changed: the choices of the use's kind again, the chosen one kept while it is listed.</summary>
    internal void PoliciesChanged()
    {
        var chosen = Policy?.Value;
        PolicyChoices = _owner.PolicyChoicesFor(Use.Value);
        Policy = PolicyChoices.FirstOrDefault(c => c.Value == chosen) ?? (chosen is null && PolicyChoices.Count > 0 ? PolicyChoices[0] : null);
        Validation.Validate();
    }

    internal void Revalidate() => Validation.Validate();

    internal void ShowServerError(string field, string error) => Validation.SetServerError(field, error);

    internal void ShowAllErrors() => Validation.ShowAll();

    internal void NotifyRole() => OnPropertyChanged(nameof(MayManage));

    protected override void OnValidationChanged() => _owner.ProfileValidationChanged();

    partial void OnNameChanged(string value) => _owner.ProfilesEdited();

    partial void OnUseChanged(Choice<ProfileUse> value)
    {
        // Another use may take another kind of policy: the first of it unless the chosen one fits.
        var chosen = Policy?.Value;
        PolicyChoices = _owner.PolicyChoicesFor(value.Value);
        Policy = PolicyChoices.FirstOrDefault(c => c.Value == chosen) ?? (PolicyChoices.Count > 0 ? PolicyChoices[0] : null);
        _owner.ProfilesEdited();
    }

    [GeneratedRegex("^[A-Za-z0-9_]{1,32}$")]
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

    /// <summary>An enrollment that issued a certificate this OADM kept: View certificate shows it.</summary>
    public bool HasCertificate => Source.Ok && !string.IsNullOrEmpty(Source.SerialNumber);

    /// <summary>Device, profile, kind, result or reason contains <paramref name="search"/> (empty: every row).</summary>
    public bool Matches(string search) =>
        search.Length == 0
        || Device.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Profile.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Kind.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Result.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Reason.Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A certificate the camera did not renew in time, as the Not renewed list shows it.</summary>
public sealed record NotRenewedRow(IssuedCertificate Source)
{
    public string Device => Source.Device;

    public string Profile => Source.Profile;

    public string ValidUntil => Source.NotAfterUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public DateTime ValidUntilSortKey => Source.NotAfterUtc;

    public string State => Source.NotAfterUtc < DateTime.UtcNow ? "Expired" : "Not renewed";
}

/// <summary>
/// The EST server page (docs/spec/est-server.md): port, profiles and Save with the status left of it; the newest
/// enrollments; the certificates the cameras did not renew in time. <see cref="Activate"/> reads the state and watches the live
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

    private readonly ICorePluginClientContext _ctx;
    private CancellationTokenSource? _active;
    private bool _profilesValid = true;

    public EstViewModel(ICorePluginClientContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        Server = new ServerSwitchViewModel(ctx, EstPluginInfo.PluginId, EstPluginInfo.DisplayName, "Cameras cannot get or renew certificates from OADM until it is on again.");
        Server.RunningChanged += (_, _) => OnServerRunningChanged();
        _ctx.PermissionsChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(MayManage));
            foreach (var row in Profiles)
            {
                row.NotifyRole();
            }

            SaveCommand.NotifyCanExecuteChanged();
            AddProfileCommand.NotifyCanExecuteChanged();
            ClearEnrollmentsCommand.NotifyCanExecuteChanged();
        };
        Validation
            .Rule(nameof(Port), () => int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1025 and <= 65535
                ? null : "Use 1025 to 65535.")
            .Rule(nameof(EnrollmentsKept), () => int.TryParse(EnrollmentsKept, NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var kept) && kept is >= 100 and <= 5000
                ? null : "Keep from 100 to 5,000 enrollments.");
        Validation.Validate();
        Listen.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InterfaceSelection.Selected))
            {
                OnPropertyChanged(nameof(SelectedInterface));
            }
        };
    }

    [ObservableProperty]
    public partial string Port { get; set; } = EstPluginInfo.DefaultPort.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial string EnrollmentsKept { get; set; } = EstPluginInfo.DefaultEnrollmentsKept.ToString(CultureInfo.InvariantCulture);

    /// <summary>The "Listen on" select (shared with the NTP and DHCP server pages).</summary>
    public InterfaceSelection Listen { get; } = new();

    public InterfaceOption? SelectedInterface
    {
        get => Listen.Selected;
        set => Listen.Selected = value;
    }

    public RangeObservableCollection<ProfileRowViewModel> Profiles { get; } = [];

    /// <summary>PKI's HTTPS and IEEE 802.1X certificate policies a profile can name (empty without PKI's policies).</summary>
    public IReadOnlyList<IssuingPolicyChoice> IssuingPolicies { get; private set; } = [];

    /// <summary>The policies a profile of <paramref name="use"/> can name, oldest first.</summary>
    internal IReadOnlyList<Choice<Guid>> PolicyChoicesFor(ProfileUse use)
    {
        var kind = EstProfile.PolicyKindOf(use);
        return [.. IssuingPolicies.Where(p => p.Kind == kind).Select(p => new Choice<Guid>(p.Id, p.Name))];
    }

    /// <summary>A site whose node address the certificate does not name; null when none.</summary>
    [ObservableProperty]
    public partial string? CertificateWarning { get; set; }

    /// <summary>The id of PKI's core plugin (Edit policies opens its page).</summary>
    public const string PkiPluginId = "oadm.pki";

    /// <summary>"2 policies apply": PKI's EST policies the server applies (Enrollment, Sign-in CA); empty for a user limited to a device group.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPolicies))]
    public partial string PoliciesText { get; set; } = string.Empty;

    /// <summary>The applied policies' names and summaries: the tooltip of <see cref="PoliciesText"/>.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<Oadm.Plugins.Pki.Policies.PolicyLine> PolicyLines { get; set; } = [];

    /// <summary>The line with "N policies apply" and Edit policies shows (users with all devices).</summary>
    public bool ShowsPolicies => PoliciesText.Length > 0;

    /// <summary>Why no camera can enroll ("Cameras cannot sign in: there is no Sign-in CA policy."); null when none.</summary>
    [ObservableProperty]
    public partial string? PolicyProblem { get; set; }

    /// <summary>"1 policy applied", "2 policies applied", "No policies applied" (the same words as the 802.1X page): the link to the policies.</summary>
    public static string PoliciesApplyText(int count) => count switch
    {
        0 => "No policies applied",
        1 => "1 policy applied",
        _ => string.Create(CultureInfo.CurrentCulture, $"{count:N0} policies applied"),
    };

    /// <summary>Edit policies: the Enrollment and Sign-in CA policies are on the PKI page, the EST server's ones highlighted.</summary>
    [RelayCommand]
    private Task EditInPkiAsync()
    {
        Oadm.Plugins.Pki.Client.PkiTabRequest.Set(Oadm.Plugins.Pki.Client.PkiViewModel.PoliciesTabId, Oadm.Plugins.Pki.Client.PolicyHighlight.Est);
        return _ctx.OpenAsync("plugin:" + PkiPluginId);
    }

    /// <summary>The enrollments that match <see cref="EnrollmentSearch"/>, newest first (bound to the grid).</summary>
    public RangeObservableCollection<EnrollmentRow> Enrollments { get; } = [];

    /// <summary>Every enrollment the server keeps (at most 5,000), newest first.</summary>
    private List<EnrollmentRow> _allEnrollments = [];

    private int? _enrollmentsKept;
    private int _loadVersion;

    [ObservableProperty]
    public partial string EnrollmentSearch { get; set; } = string.Empty;

    /// <summary>The enrollment selected in the list: View certificate shows its certificate.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ViewCertificateCommand))]
    public partial EnrollmentRow? SelectedEnrollment { get; set; }

    /// <summary>The view opens the details window (<see cref="EstCertificateWindow"/>) with this.</summary>
    public event EventHandler<CertificateDetails>? CertificateRequested;

    private bool CanViewCertificate => SelectedEnrollment is { HasCertificate: true };

    /// <summary>"View certificate" (and a double click on a row): the certificate the enrollment issued, read from the server.</summary>
    [RelayCommand(CanExecute = nameof(CanViewCertificate))]
    private async Task ViewCertificateAsync()
    {
        if (SelectedEnrollment is not { HasCertificate: true } row)
        {
            return;
        }

        try
        {
            var json = await _ctx.InvokeAsync(EstMethods.GetCertificate, EstJson.Serialize(new CertificateQuery(row.Source.SerialNumber!)), CancellationToken.None).ConfigureAwait(true);
            CertificateRequested?.Invoke(this, new CertificateDetails(EstJson.Deserialize<CertificateDescription>(json), $"Certificate · {row.Device} · {row.Profile}", DateTime.UtcNow));
        }
#pragma warning disable CA1031 // Shown to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await _ctx.ShowMessageAsync("View certificate", ServerErrors.Message(ex)).ConfigureAwait(true);
        }
    }

    /// <summary>The certificates the cameras did not renew in time (the section shows only when there are any).</summary>
    public RangeObservableCollection<NotRenewedRow> NotRenewed { get; } = [];

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Loading";

    [ObservableProperty]
    public partial bool StatusOk { get; set; }

    /// <summary>The listeners that do not run, one per line ("EST server at Building B: waiting for approval in the OADM Site Node app").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasListenerProblems))]
    public partial string ListenersText { get; set; } = string.Empty;

    public bool HasListenerProblems => ListenersText.Length > 0;

    /// <summary>"2 certificates" or "Showing 500 of 1,204".</summary>
    [ObservableProperty]
    public partial string NotRenewedSummary { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; set; }

    /// <summary>A save failed: the reason, below the Save button's row.</summary>
    [ObservableProperty]
    public partial string? ActionError { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>The role may change the EST server ("manage") and the user has all devices (the server is server-wide).</summary>
    public bool MayManage => _ctx.HasAllDevices && _ctx.Has(PermissionKeys.Of(EstPluginInfo.PluginId, PermissionKeys.Manage));

    public bool HasEnrollments => _allEnrollments.Count > 0;

    /// <summary>"No enrollments yet ..." or "No enrollment matches the search."; null while rows show.</summary>
    public string? EnrollmentsEmptyText =>
        Enrollments.Count > 0 ? null
        : _allEnrollments.Count == 0 ? "No enrollments yet. Set up a camera with Certificate enrollment."
        : "No enrollment matches the search.";

    public bool HasNotRenewed => NotRenewed.Count > 0;

    public bool CanAddProfile => MayManage && Profiles.Count < Uses.Count;

    /// <summary>Why Save is off, for its tooltip.</summary>
    public string? SaveTip => !IsFormValid ? FormError : Profiles.Select(p => p.FormError).FirstOrDefault(e => e is not null);

    /// <summary>Turn on / Turn off of the server (PKI): the cards show only while it runs.</summary>
    public ServerSwitchViewModel Server { get; }

    public void Activate()
    {
        Deactivate();
        _active = new CancellationTokenSource();
        var ct = _active.Token;
        IsLoading = true;
        _ = LoadServerAsync(ct);
        _ = _ctx.RunAsync(OnEvent, null, ct);
    }

    /// <summary>The switch first, then the cards while the server runs.</summary>
    internal async Task LoadServerAsync(CancellationToken ct)
    {
        _readingSwitch = true;
        try
        {
            await Server.LoadAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // The page shows the reason instead of crashing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            StatusText = ServerErrors.Message(ex);
            StatusOk = false;
        }
        finally
        {
            _readingSwitch = false;
        }

        if (Server.Running && !ct.IsCancellationRequested)
        {
            await LoadAsync(ct).ConfigureAwait(true);
        }
        else
        {
            IsLoading = false;
        }
    }

    /// <summary>The switch is being read by <see cref="LoadServerAsync"/>, which reads the cards itself.</summary>
    private bool _readingSwitch;

    /// <summary>The server started (read the cards) or stopped (the page shows only the switch).</summary>
    private void OnServerRunningChanged()
    {
        if (_readingSwitch)
        {
            return;
        }

        if (Server.Running && _active is { } active)
        {
            IsLoading = true;
            _ = LoadAsync(active.Token);
        }
        else if (!Server.Running)
        {
            ++_loadVersion; // a read still running no longer owns the overlay
            IsLoading = false;
        }
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
        var version = ++_loadVersion; // a later load (the page shown again) owns the overlay
        try
        {
            ApplyState(EstJson.Deserialize<EstState>(await _ctx.InvokeAsync(EstMethods.GetState, null, ct).ConfigureAwait(true)), form: true);
            var enrollments = EstJson.Deserialize<EnrollmentList>(await _ctx.InvokeAsync(EstMethods.GetEnrollments, null, ct).ConfigureAwait(true));
            // Rows that arrived live during the read and are newer than its newest stay on top.
            var newest = enrollments.Enrollments.Count > 0 ? enrollments.Enrollments[0].TimeUtc : DateTime.MinValue;
            var live = _allEnrollments.Where(r => r.Source.TimeUtc > newest).ToList();
            _allEnrollments = [.. live, .. enrollments.Enrollments.Select(e => new EnrollmentRow(e))];
            FilterEnrollments();
            await LoadNotRenewedAsync(ct).ConfigureAwait(true);
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
            if (version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }

    internal async Task LoadNotRenewedAsync(CancellationToken ct)
    {
        var list = EstJson.Deserialize<CertificateList>(await _ctx.InvokeAsync(EstMethods.GetNotRenewed, null, ct).ConfigureAwait(true));
        NotRenewed.ReplaceAll(list.Certificates.Select(c => new NotRenewedRow(c)));
        NotRenewedSummary = list.Total > list.Certificates.Count
            ? string.Create(CultureInfo.CurrentCulture, $"Showing {list.Certificates.Count:N0} of {list.Total:N0}")
            : list.Total == 1 ? "1 certificate" : string.Create(CultureInfo.CurrentCulture, $"{list.Total:N0} certificates");
        OnPropertyChanged(nameof(HasNotRenewed));
    }

    [RelayCommand(CanExecute = nameof(CanAddProfile))]
    private void AddProfile()
    {
        var use = Uses.First(u => Profiles.All(p => p.Use.Value != u.Value)).Value;
        Profiles.Add(new ProfileRowViewModel(this, new EstProfile(use.ToString().ToLowerInvariant(), use)));
        ProfilesEdited();
    }

    [RelayCommand]
    private async Task RemoveProfileAsync(ProfileRowViewModel? row)
    {
        if (row is null || !MayManage || Profiles.Count <= 1)
        {
            return;
        }

        // Confirmed first (red Remove, decision 2026-10-25); the change takes effect with Save like every other field.
        var name = row.Name.Trim();
        if (!await _ctx.ConfirmAsync("Remove profile", $"Remove the profile {name}? After Save, cameras that use it can no longer get or renew certificates with it.", "Remove").ConfigureAwait(true))
        {
            return;
        }

        if (Profiles.Remove(row))
        {
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
                InterfaceId = Listen.SelectedId ?? InterfaceOptions.AllId,
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

    partial void OnEnrollmentSearchChanged(string value) => FilterEnrollments();

    /// <summary>One pass over at most 5,000 rows and one reset of the grid.</summary>
    private void FilterEnrollments()
    {
        var search = EnrollmentSearch.Trim();
        Enrollments.ReplaceAll(search.Length == 0 ? _allEnrollments : _allEnrollments.Where(r => r.Matches(search)));
        NotifyEnrollments();
    }

    private void NotifyEnrollments()
    {
        OnPropertyChanged(nameof(HasEnrollments));
        OnPropertyChanged(nameof(EnrollmentsEmptyText));
        ClearEnrollmentsCommand.NotifyCanExecuteChanged();
    }

    private bool CanClearEnrollments => MayManage && HasEnrollments;

    [RelayCommand(CanExecute = nameof(CanClearEnrollments))]
    private async Task ClearEnrollmentsAsync()
    {
        if (!await _ctx.ConfirmAsync("Delete all", "Delete every entry of the enrollments list? Other clients see the empty list too.", "Delete all").ConfigureAwait(true))
        {
            return;
        }

        ActionError = null;
        try
        {
            // Only the rows shown when the server deleted: one that arrived live meanwhile stays (the clear event of the
            // server empties every page anyway, in order with the new rows).
            var deleted = _allEnrollments.ToHashSet(ReferenceEqualityComparer.Instance);
            await _ctx.InvokeAsync(EstMethods.ClearEnrollments, null, CancellationToken.None).ConfigureAwait(true);
            _allEnrollments.RemoveAll(deleted.Contains);
            FilterEnrollments();
        }
#pragma warning disable CA1031 // The page shows the reason.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ActionError = ex.Message;
        }
    }

    private async Task ReloadNotRenewedAsync()
    {
        try
        {
            await LoadNotRenewedAsync(_active?.Token ?? CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // A failed read leaves the list as it was.
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
                "policy" => nameof(ProfileRowViewModel.Policy),
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
            case "interface":
                Validation.SetServerError(nameof(SelectedInterface), error);
                break;
            default:
                ActionError = error;
                break;
        }
    }

    private void ApplyState(EstState state, bool form)
    {
        _enrollmentsKept = state.Config.EnrollmentsKept;
        StatusText = state.StatusText;
        StatusOk = state.StatusOk;
        // The sites' relays only: this server's own listener is the status chip already.
        ListenersText = string.Join(Environment.NewLine, state.Listeners.Where(l => !l.Ok && l.SiteId is not null).Select(l => l.Text));
        Listen.Apply(state.Interfaces, state.Config.InterfaceId, state.Config.InterfaceName);
        CertificateWarning = state.CertificateWarning;
        PoliciesText = state.PoliciesApply is { } count ? PoliciesApplyText(count) : string.Empty;
        PolicyLines = state.PolicyLines ?? [];
        PolicyProblem = state.PolicyProblem;
        if (!IssuingPolicies.SequenceEqual(state.IssuingPolicies))
        {
            IssuingPolicies = state.IssuingPolicies;
            foreach (var row in Profiles)
            {
                row.PoliciesChanged();
            }
        }

        if (!form)
        {
            return;
        }

        Listen.Select(state.Config.InterfaceId);
        Port = state.Config.Port.ToString(CultureInfo.InvariantCulture);
        EnrollmentsKept = state.Config.EnrollmentsKept.ToString(CultureInfo.InvariantCulture);
        Profiles.ReplaceAll(state.Config.Profiles.Select(p => new ProfileRowViewModel(this, p)));
        Validation.Reset();
        ProfilesEdited();
    }

    private void OnEvent(PluginEvent e)
    {
        if (Server.OnEvent(e))
        {
            return; // turned on or off, or the license changed
        }

        switch (e.Topic)
        {
            case EstMethods.StateTopic:
                ApplyState(EstJson.Deserialize<EstState>(e.PayloadJson), form: false);
                break;
            case EstMethods.EnrollmentsTopic:
                var added = EstJson.Deserialize<EnrollmentList>(e.PayloadJson).Enrollments.Select(x => new EnrollmentRow(x)).ToList();
                _allEnrollments.InsertRange(0, added);
                if (_enrollmentsKept is { } kept && _allEnrollments.Count > kept) // the saved setting, never an unsaved field
                {
                    _allEnrollments.RemoveRange(kept, _allEnrollments.Count - kept);
                    FilterEnrollments();
                }
                else
                {
                    Enrollments.InsertRange(0, added.Where(a => a.Matches(EnrollmentSearch.Trim())));
                    NotifyEnrollments();
                }
                if (added.Any(a => a.Ok) && HasNotRenewed)
                {
                    _ = ReloadNotRenewedAsync(); // a late renewal ends a Not renewed
                }

                break;
            case EstMethods.EnrollmentsClearedTopic:
                _allEnrollments = [];
                FilterEnrollments();
                break;
            case EstMethods.CertificatesTopic:
                _ = ReloadNotRenewedAsync();
                break;
        }
    }

    [GeneratedRegex(@"^profiles\[(\d+)\]\.(\w+)$")]
    private static partial Regex ProfileField();
}
