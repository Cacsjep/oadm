using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Certificates;

namespace Oadm.Sdk.Client.Controls;

/// <summary>A choice of a filter of <see cref="IssuedCertificateListViewModel"/>: the value sent to the server and its label.</summary>
/// <param name="Value">Empty for "every".</param>
/// <param name="Label">The text of the choice.</param>
/// <param name="IsIssuer">A CA of the "Issued by" filter (<see cref="IssuedCertificateQuery.IssuedBy"/>); false: a source (Task, EST).</param>
public sealed record IssuedCertificateFilter(string Value, string Label, bool IsIssuer = false)
{
    public override string ToString() => Label;
}

/// <summary>
/// A status a page adds to a row's State column (SDK 0.4.0), e.g. "Installed on the server" of the OADM CA: a
/// <see cref="StatusChip"/> below the row's state.
/// </summary>
/// <param name="Text">The status in plain words.</param>
/// <param name="Kind">"ok", "warning", "error", "accent" (running) or anything else for neutral.</param>
/// <param name="Tip">The tooltip (the reason of a failure); null for none.</param>
public sealed record IssuedCertificateStatus(string Text, string Kind, string? Tip = null)
{
    /// <summary>Green.</summary>
    public bool IsOk => Kind == "ok";

    /// <summary>Amber.</summary>
    public bool IsWarning => Kind == "warning";

    /// <summary>Red.</summary>
    public bool IsError => Kind == "error";

    /// <summary>Running (the accent color).</summary>
    public bool IsAccent => Kind == "accent";
}

/// <summary>
/// One entry of a row's actions menu (SDK 0.4.0; the row's ⋯ button and its right-click menu, one source:
/// <see cref="IssuedCertificateListViewModel.MenuOf"/>): its text, command and parameter, red for one that deletes, revokes or
/// removes, or a submenu (<see cref="Items"/>). <see cref="Separator"/> is the line between the everyday and the red ones.
/// </summary>
/// <param name="Text">The entry's text ("Export", "Revoke").</param>
/// <param name="Command">What the entry runs; null for a submenu.</param>
/// <param name="Parameter">The command's parameter (the row).</param>
/// <param name="IsDanger">Red: the entry deletes, revokes or removes.</param>
/// <param name="Items">The entries of a submenu; null for a plain entry.</param>
public sealed record IssuedCertificateAction(string Text, System.Windows.Input.ICommand? Command = null, object? Parameter = null, bool IsDanger = false,
    IReadOnlyList<IssuedCertificateAction>? Items = null)
{
    /// <summary>The line between groups of entries.</summary>
    public static IssuedCertificateAction Separator { get; } = new("-");

    /// <summary>The entry is <see cref="Separator"/>.</summary>
    public bool IsSeparator => ReferenceEquals(this, Separator);
}

/// <summary>One row of the list of issued certificates (texts for the grid; the server's row in <see cref="Source"/>).</summary>
public sealed class IssuedCertificateRow(IssuedCertificateInfo source)
{
    /// <summary>The row as the server sent it.</summary>
    public IssuedCertificateInfo Source { get; } = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>The row's id: the serial number of a device certificate, else <see cref="IssuedCertificateInfo.Id"/>.</summary>
    public string Key => Source.Id ?? Source.SerialNumber;

    /// <summary>The device's address; for a device OADM no longer manages its serial, "B8A44F631339 (removed)".</summary>
    public string Device => Source.DeviceManaged ? Source.Device : IssuedCertificateTexts.RemovedDevice(Source.DeviceSerial);

    /// <summary>The certificate's name, or the device of a device certificate (the first column).</summary>
    public string Name => Source.Name ?? Device;

    /// <summary>"HTTPS", "IEEE 802.1X", "MQTT", "RTSPS", "Server", "CA", "Other".</summary>
    public string Use => ListedCertificateUses.Text(Source.Purpose);

    /// <summary>The CA that issued it; "Task" or "EST" from a server that does not say.</summary>
    public string IssuedBy => Source.IssuedBy ?? Source.Source;

    /// <summary>The Issuer column: the CA that issued it; "Self-signed" for a root CA; "-" when unknown.</summary>
    public string Issuer => Source.Kind == ListedCertificateKind.Authority && Source.IsRootCa ? IssuedCertificateMessages.SelfSigned
        : string.IsNullOrEmpty(IssuedBy) ? "-" : IssuedBy;

    /// <summary>The Key column: "RSA 4096", "ECC P-256"; "-" when unknown.</summary>
    public string KeyType => string.IsNullOrEmpty(Source.KeyText) ? "-" : Source.KeyText;

    /// <summary>The device it is installed on, "802.1X server", "EST server"; "-" when nothing uses it.</summary>
    public string UsedBy => string.IsNullOrEmpty(Source.UsedBy) ? "-" : Source.UsedBy;

    /// <summary>The Used by column as badges (a CA's groups, a server certificate's server); empty: <see cref="UsedByText"/>.</summary>
    public IReadOnlyList<string> UsedByBadges => Source.UsedByBadges ?? [];

    /// <summary>The Used by column as text (the device, "-") when there are no badges; null with badges.</summary>
    public string? UsedByText => UsedByBadges.Count > 0 ? null : UsedBy;

    /// <summary>The Used by cell's tooltip: every use, one per line ("802.1X: OADM CA (...)", "Issuing: HTTPS, ..."); null for "-".</summary>
    public string? UsedByTip => string.IsNullOrEmpty(Source.UsedBy) ? null : Source.UsedBy.Replace("; ", Environment.NewLine, StringComparison.Ordinal);

    /// <summary>
    /// The Used in column of a CA list (<see cref="IssuedCertificateListViewModel.ShowsUsedIn"/>): the names of the policies
    /// that use the CA, at most <see cref="IssuedCertificateTexts.UsedInShown"/> as badges, then "+N"; empty for none.
    /// </summary>
    public IReadOnlyList<string> UsedIn => _usedIn ??= IssuedCertificateTexts.UsedInBadges(Source.UsedIn ?? []);

    private IReadOnlyList<string>? _usedIn;

    /// <summary>The Used in cell's tooltip: every policy, one per line; null for none.</summary>
    public string? UsedInTip => Source.UsedIn is { Count: > 0 } names ? string.Join(Environment.NewLine, names) : null;

    /// <summary>Start of validity, "2026-10-24".</summary>
    public string ValidFrom => Source.NotBeforeUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>End of validity, "2027-10-24".</summary>
    public string ValidUntil => Source.NotAfterUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>"7D FD 05 ..." (pairs, like the certificate window).</summary>
    public string SerialNumber => IssuedCertificateTexts.Pairs(Source.SerialNumber);

    /// <summary>"Valid", "Expires soon", "Expired", "Revoked" ("Replaced" only from a server before OADM 4.5).</summary>
    public string State => IssuedCertificateTexts.State(Source.State);

    /// <summary>The time the row was read (the days left of <see cref="StateText"/>); null: the state alone.</summary>
    public DateTime? NowUtc { get; init; }

    /// <summary>
    /// The badge below the date of Valid until, one line: "Expires in 12 days" when it ends within its Expiry warning days,
    /// "Expired", "Revoked"; a valid certificate has none (<see cref="HasStateBadge"/>).
    /// </summary>
    public string StateText => Source.State == IssuedCertificateState.ExpiresSoon && DaysLeft is { } days ? IssuedCertificateTexts.ExpiresIn(days) : State;

    /// <summary>The state needs attention: everything but a plain valid certificate.</summary>
    public bool HasStateBadge => Source.State != IssuedCertificateState.Valid;

    /// <summary>Whole days until the end (0 on the last day); null without <see cref="NowUtc"/> or after the end.</summary>
    public int? DaysLeft => NowUtc is { } now && Source.NotAfterUtc > now ? (int)Math.Floor((Source.NotAfterUtc - now).TotalDays) : null;

    /// <summary>"Certificate revoked on 2026-11-02 (Key compromised) by admin" for a revoked one; null otherwise.</summary>
    public string? StateTip => Source.Revocation is { } r ? r.Describe() + " by " + r.UserName : null;

    /// <summary>Statuses the page adds below the state (the OADM CA: where it is trusted); empty for most rows.</summary>
    public IReadOnlyList<IssuedCertificateStatus> MoreStates { get; init; } = [];

    /// <summary><see cref="MoreStates"/> has entries.</summary>
    public bool HasMoreStates => MoreStates.Count > 0;

    /// <summary>The group header of the row's group ("Device certificates · 60,000"); null without groups.</summary>
    public string? GroupTitle { get; init; }

    /// <summary>
    /// The Type column: what the row is, as badges. A CA: Root CA or Intermediate CA and its kind (OADM CA, Previous OADM CA,
    /// Axis device ID, Imported); a server's certificate: Server; one made here: its use and Created, Signed or Imported; a
    /// device certificate: its use.
    /// </summary>
    public IReadOnlyList<string> Badges => _badges ??= IssuedCertificateTexts.Badges(Source);

    private IReadOnlyList<string>? _badges;

    /// <summary>
    /// The second line of the name: the key and the issuer ("RSA 4096 · issued by OADM Root CA SERVER01"; a root CA its key
    /// only; a row without key text "Issued by ..."); null when there is nothing to say.
    /// </summary>
    public string? Detail => _detail ??= IssuedCertificateTexts.Detail(Source) ?? string.Empty;

    private string? _detail;

    /// <summary><see cref="Detail"/> has text.</summary>
    public bool HasDetail => Detail is { Length: > 0 };

    /// <summary>The row's kind of CA; null for a row that is no CA.</summary>
    public CertificateAuthorityKind? AuthorityKind => Source.Kind == ListedCertificateKind.Authority ? Source.AuthorityKind : null;

    /// <summary>Valid: green check.</summary>
    public bool IsOk => Source.State == IssuedCertificateState.Valid;

    /// <summary>Expires soon: amber.</summary>
    public bool IsWarning => Source.State == IssuedCertificateState.ExpiresSoon;

    /// <summary>Expired or revoked: red.</summary>
    public bool IsError => Source.State is IssuedCertificateState.Expired or IssuedCertificateState.Revoked;

    /// <summary>Replaced (a server before OADM 4.5): grey.</summary>
    public bool IsNeutral => Source.State == IssuedCertificateState.Replaced;

    /// <summary>A CA row of the OADM CA that issues now.</summary>
    public bool IsOadmCa => AuthorityKind == CertificateAuthorityKind.Oadm;

    /// <summary>A CA row that can be removed (previous OADM CAs and imported CAs; the server refuses one a policy names).</summary>
    public bool IsRemovableCa => AuthorityKind is CertificateAuthorityKind.PreviousOadm or CertificateAuthorityKind.Imported;

    /// <summary>A row that is no CA: Export with its key, Revoke and Delete belong to these.</summary>
    public bool IsCertificate => Source.Kind != ListedCertificateKind.Authority;

    /// <summary>Issued by the OADM CA, still valid and not revoked: Revoke can take it.</summary>
    public bool CanRevoke => Source.Kind is ListedCertificateKind.Device or ListedCertificateKind.Created or ListedCertificateKind.SignedRequest
        && Source.State is IssuedCertificateState.Valid or IssuedCertificateState.ExpiresSoon or IssuedCertificateState.Replaced;

    /// <summary>
    /// Nothing uses it: a revoked or expired certificate, or one imported here that no server uses (created and signed ones
    /// are the OADM CA's: revoked first). CAs are removed in their own list. The server checks again.
    /// </summary>
    public bool CanDelete => Source.Kind switch
    {
        ListedCertificateKind.Authority => false,
        ListedCertificateKind.Imported => string.IsNullOrEmpty(Source.UsedBy) || IsEnded,
        _ => IsEnded,
    };

    /// <summary>OADM holds the key: PKCS#12 can be exported.</summary>
    public bool HasKey => Source.HasKey;

    private bool IsEnded => Source.State is IssuedCertificateState.Revoked or IssuedCertificateState.Expired;
}

/// <summary>The texts of the list of issued certificates and of a revocation, one phrase each (PKI, 802.1X server).</summary>
public static class IssuedCertificateTexts
{
    /// <summary>The choices of the reason of Revoke, in the page's order.</summary>
    public static IReadOnlyList<RevocationReason> Reasons { get; } =
        [RevocationReason.KeyCompromised, RevocationReason.DeviceReplaced, RevocationReason.DeviceRemoved, RevocationReason.Other];

    /// <summary>The confirmation of several certificates.</summary>
    public const string ConfirmMessage = "OADM no longer accepts them for network access or renewal. This cannot be undone.";

    /// <summary>The confirmation of one certificate.</summary>
    public const string ConfirmMessageOne = "OADM no longer accepts it for network access or renewal. This cannot be undone.";

    /// <summary>The answer when every chosen certificate was revoked or expired already.</summary>
    public const string NothingRevoked = "Nothing was revoked: these certificates are already revoked or expired.";

    /// <summary>The tooltip of a row's ⋯ button.</summary>
    public const string ActionsTip = "Actions";

    /// <summary>The empty list without filters.</summary>
    public const string NoCertificates = "No certificates yet. Enable HTTPS or IEEE 802.1X on devices, or set up Certificate enrollment.";

    /// <summary>The confirmation of Delete.</summary>
    public const string DeleteMessage = "They leave the list. A revocation stays in force. This cannot be undone.";

    /// <summary>The confirmation of Delete of one certificate.</summary>
    public const string DeleteMessageOne = "It leaves the list. A revocation stays in force. This cannot be undone.";

    /// <summary>The empty list with a filter or a search.</summary>
    public const string NoMatch = "No certificate matches.";

    /// <summary>A serial that is not listed (or of a device outside the user's group).</summary>
    public const string NotListed = IssuedCertificateMessages.NotListed;

    /// <summary>A certificate listed by OADM 4.1, before OADM kept a copy of each one.</summary>
    public const string NoCopy = IssuedCertificateMessages.NoCopy;

    /// <summary>The overlay and the progress row while the list is read.</summary>
    public const string Reading = "Reading the certificates";

    /// <summary>The progress row while Revoke runs.</summary>
    public const string Revoking = "Revoking the certificates";

    /// <summary>A revoked serial the list did not hold: OADM does not know its device.</summary>
    public const string UnknownDevice = "Unknown device";

    /// <summary>The days before the end that count as "Expires soon" when the server does not say (the settings' default).</summary>
    public const int DefaultWarningDays = 30;

    /// <summary>A device OADM no longer manages: "B8A44F631339 (removed)", or "Device (removed)" without its serial.</summary>
    public static string RemovedDevice(string? deviceSerial) => (string.IsNullOrEmpty(deviceSerial) ? "Device" : deviceSerial) + " (removed)";

    /// <summary>"Expires in 12 days", "Expires in 1 day", "Expires today".</summary>
    public static string ExpiresIn(int days) => days switch
    {
        0 => "Expires today",
        1 => "Expires in 1 day",
        _ => string.Create(CultureInfo.CurrentCulture, $"Expires in {days:N0} days"),
    };

    /// <summary>"Device certificates · 60,000": a group header.</summary>
    public static string GroupHeader(string title, int count) => string.Create(CultureInfo.CurrentCulture, $"{title} · {count:N0}");

    /// <summary>The badge of an imported CA with its key: its policies can pick it as the CA that issues.</summary>
    public const string CanIssue = "Can issue";

    /// <summary>The policy names a Used in cell shows as badges before "+N" (one: the column stays narrow, the tooltip lists all).</summary>
    public const int UsedInShown = 1;

    /// <summary>The badges of a Used in cell: the first <see cref="UsedInShown"/> names, then "+N" for the rest.</summary>
    public static IReadOnlyList<string> UsedInBadges(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return names.Count <= UsedInShown
            ? names
            : [.. names.Take(UsedInShown), string.Create(CultureInfo.CurrentCulture, $"+{names.Count - UsedInShown:N0}")];
    }

    /// <summary>"OADM CA", "Previous OADM CA", "Axis device ID", "Imported".</summary>
    public static string AuthorityKindText(CertificateAuthorityKind kind) => kind switch
    {
        CertificateAuthorityKind.Oadm => "OADM CA",
        CertificateAuthorityKind.PreviousOadm => "Previous OADM CA",
        CertificateAuthorityKind.AxisDeviceId => "Axis device ID",
        _ => "Imported",
    };

    /// <summary>The badges of the Type column (<see cref="IssuedCertificateRow.Badges"/>).</summary>
    public static IReadOnlyList<string> Badges(IssuedCertificateInfo row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var use = row.Purpose is ListedCertificateUses.Other or ListedCertificateUses.Ca ? null : ListedCertificateUses.Text(row.Purpose);
        return row.Kind switch
        {
            ListedCertificateKind.Authority =>
            [
                row.IsRootCa ? "Root CA" : "Intermediate CA",
                .. row.AuthorityKind is { } kind ? [AuthorityKindText(kind)] : Array.Empty<string>(),
                .. row.AuthorityKind == CertificateAuthorityKind.Imported && row.CanIssue ? [CanIssue] : Array.Empty<string>(),
            ],
            ListedCertificateKind.Server => [ListedCertificateUses.Text(ListedCertificateUses.Server)],
            ListedCertificateKind.Created => [.. Use(use), "Created"],
            ListedCertificateKind.SignedRequest => [.. Use(use), "Signed"],
            ListedCertificateKind.Imported => [.. Use(use), "Imported"],
            _ => [.. Use(use)],
        };

        static string[] Use(string? text) => string.IsNullOrEmpty(text) ? [] : [text];
    }

    /// <summary>The second line of the name (<see cref="IssuedCertificateRow.Detail"/>).</summary>
    public static string? Detail(IssuedCertificateInfo row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var issuer = row.IssuedBy ?? row.Source;
        var self = row.Kind == ListedCertificateKind.Authority && row.IsRootCa;
        return (row.KeyText, self || string.IsNullOrEmpty(issuer)) switch
        {
            ({ Length: > 0 } key, true) => key,
            ({ Length: > 0 } key, false) => key + " · issued by " + issuer,
            (_, false) => "Issued by " + issuer,
            _ => null,
        };
    }

    /// <summary>"State" texts.</summary>
    public static string State(IssuedCertificateState state) => state switch
    {
        IssuedCertificateState.ExpiresSoon => "Expires soon",
        IssuedCertificateState.Expired => "Expired",
        IssuedCertificateState.Replaced => "Replaced",
        IssuedCertificateState.Revoked => "Revoked",
        _ => "Valid",
    };

    /// <summary>The title of the delete confirmation: "Delete the certificate nvr.example.com?" or "Delete 3 certificates?".</summary>
    public static string DeleteTitle(IReadOnlyList<IssuedCertificateRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Count == 1
            ? $"Delete the certificate {rows[0].Name}?"
            : string.Create(CultureInfo.CurrentCulture, $"Delete {rows.Count:N0} certificates?");
    }

    /// <summary>"7DFD05" as "7D FD 05".</summary>
    public static string Pairs(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        var even = hex.Length % 2 == 1 ? "0" + hex : hex;
        return string.Join(' ', Enumerable.Range(0, even.Length / 2).Select(i => even.Substring(i * 2, 2)));
    }

    /// <summary>The title of the revoke confirmation: "Revoke the certificate of 10.0.0.48?" or "Revoke 3 certificates?".</summary>
    public static string RevokeTitle(IReadOnlyList<IssuedCertificateRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows.Count == 1
            ? $"Revoke the certificate of {rows[0].Device}?"
            : string.Create(CultureInfo.CurrentCulture, $"Revoke {rows.Count:N0} certificates?");
    }
}

/// <summary>One page of the list as the server answered it.</summary>
/// <param name="Rows">The certificates of the page, newest first.</param>
/// <param name="Total">How many match the filters and the search.</param>
/// <param name="Offset">The first row of the page (the server may move it back when the list became shorter).</param>
public sealed record IssuedCertificateListPage(IReadOnlyList<IssuedCertificateInfo> Rows, int Total, int Offset)
{
    /// <summary>
    /// The days before the end that the server counts as "Expires soon" (PKI's setting); null when the server does not say:
    /// the certificate window then follows the row's state.
    /// </summary>
    public int? WarningDays { get; init; }

    /// <summary>
    /// The CAs of the "Issued by" filter; null when the server does not list them (the filter then offers Task and EST).
    /// </summary>
    public IReadOnlyList<CertificateIssuerChoice>? Issuers { get; init; }

    /// <summary>The groups of every page with their counts, in their order; null or empty: no group headers.</summary>
    public IReadOnlyList<IssuedCertificateGroup>? Groups { get; init; }
}

/// <summary>A choice of "Group by" (<see cref="IssuedCertificateListViewModel.GroupChoices"/>).</summary>
/// <param name="Value">The grouping sent to the server.</param>
/// <param name="Label">The text of the choice ("Group by type").</param>
public sealed record IssuedCertificateGroupChoice(IssuedCertificateGrouping Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One certificate for View certificate: the description and its revocation, or why it cannot be shown.</summary>
/// <param name="Certificate">The certificate (<see cref="CertificateDescriber.Describe"/>); null when it cannot be shown.</param>
/// <param name="Revocation">Its revocation, when it is revoked.</param>
/// <param name="Error">Why it cannot be shown ("OADM keeps no copy of this certificate.").</param>
public sealed record IssuedCertificateDetails(CertificateDescription? Certificate, CertificateRevocation? Revocation, string? Error);

/// <summary>Where <see cref="IssuedCertificateListViewModel"/> reads (the PKI page from PKI, the 802.1X page through its server).</summary>
public interface IIssuedCertificateSource
{
    /// <summary>One page with the query's filters, search, offset and limit.</summary>
    Task<IssuedCertificateListPage> ReadAsync(IssuedCertificateQuery query, CancellationToken ct);

    /// <summary>The certificate of a row (<see cref="IssuedCertificateRow.Key"/>: its serial number or id) for View certificate.</summary>
    Task<IssuedCertificateDetails> GetCertificateAsync(string serialNumber, CancellationToken ct);
}

/// <summary>What the list asks of its view: the shared certificate window.</summary>
public interface IIssuedCertificateListDialogs
{
    /// <summary>Shows <paramref name="certificate"/> in the shared <see cref="CertificateWindow"/>.</summary>
    Task ShowCertificateAsync(CertificateDetails certificate);
}

/// <summary>
/// The list of certificates the OADM CA issued (SDK 0.4.0; the PKI Certificates tab and the certificate choice of the
/// 802.1X server's Revoke): newest first, read from the server one page at a time with the filters (state, use, issued by)
/// and the search (it waits for the typing to stop), the grid's selection in one step, View certificate. Shown by
/// <see cref="IssuedCertificateList"/>; the first read shows the overlay, later ones the progress row.
/// </summary>
public partial class IssuedCertificateListViewModel : ObservableObject
{
    private readonly IIssuedCertificateSource _source;
    private readonly List<IssuedCertificateRow> _selected = [];
    private CancellationTokenSource? _searchDelay;
    private int _readVersion;
    private bool _loadedOnce;
    private bool _started;
    private int? _warningDays;
    private bool _applyingChoices;

    /// <summary>A list that reads from <paramref name="source"/>; <paramref name="time"/> for the search delay (tests).</summary>
    public IssuedCertificateListViewModel(IIssuedCertificateSource source, TimeProvider? time = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        Time = time ?? TimeProvider.System;
        StateFilter = StateChoices[0];
        UseFilter = UseChoices[0];
        SourceChoices.AddRange(SourceChoicesOfTasks);
        SourceFilter = SourceChoices[0];
        GroupBy = GroupChoices[0];
    }

    /// <summary>"Group by type" (the default), "Group by issuer", "No groups".</summary>
    public IReadOnlyList<IssuedCertificateGroupChoice> GroupChoices { get; } =
    [
        new(IssuedCertificateGrouping.Type, "Group by type"),
        new(IssuedCertificateGrouping.Issuer, "Group by issuer"),
        new(IssuedCertificateGrouping.None, "No groups"),
    ];

    /// <summary>
    /// Whether the list offers Group by (a source that groups on the server, the PKI page); false: the select is hidden and
    /// the rows have no group headers.
    /// </summary>
    public bool CanGroup { get; init; }

    /// <summary>The filters show (default); false for a short list such as the PKI page's CAs (the search always shows).</summary>
    public bool ShowsFilters { get; init; } = true;

    /// <summary>
    /// A CA list (the PKI page's): the Used in column shows (the policies that use each CA) and the Type column is wider (the
    /// OADM CA's badges); false by default.
    /// </summary>
    public bool ShowsUsedIn { get; init; }

    /// <summary>The chosen grouping; a change reads the first page again.</summary>
    [ObservableProperty]
    public partial IssuedCertificateGroupChoice GroupBy { get; set; }

    /// <summary>The rows have group headers (<see cref="IssuedCertificateRow.GroupTitle"/>).</summary>
    [ObservableProperty]
    public partial bool IsGrouped { get; private set; }

    /// <summary>The group headers of the shown page in their order (the view rebuilds its groups when they change).</summary>
    public IReadOnlyList<string> GroupTitles { get; private set; } = [];

    /// <summary>The groups of the shown page changed (another grouping, other counts): the view groups the rows again.</summary>
    public event EventHandler? GroupsChanged;

    private IReadOnlyList<IssuedCertificateGroup> _groups = [];

    partial void OnGroupByChanged(IssuedCertificateGroupChoice value)
    {
        if (_started)
        {
            OnGroupingChanged(value.Value);
        }

        Refilter();
    }

    /// <summary>After the user chose another grouping (a page keeps the choice).</summary>
    protected virtual void OnGroupingChanged(IssuedCertificateGrouping grouping)
    {
    }

    /// <summary>
    /// The actions menu of a row (its ⋯ button and its right-click menu): the row's own actions, or, when the row is one of
    /// several chosen rows, what the page does with all of them (<see cref="SelectionActions"/>) when it offers something.
    /// </summary>
    public IReadOnlyList<IssuedCertificateAction> MenuOf(IssuedCertificateRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return _selected.Count > 1 && _selected.Contains(row) && SelectionActions() is { Count: > 0 } several ? several : ActionsOf(row);
    }

    /// <summary>The actions of one row: View by default; a page adds its own (everyday ones first, a separator, the red ones last).</summary>
    protected virtual IReadOnlyList<IssuedCertificateAction> ActionsOf(IssuedCertificateRow row) => [new("View", ViewRowCommand, row)];

    /// <summary>The actions for several chosen rows; none by default (the menu then shows the row's own).</summary>
    protected virtual IReadOnlyList<IssuedCertificateAction> SelectionActions() => [];

    /// <summary>Statuses the page adds below the type of a row (the OADM CA's trust); none by default.</summary>
    protected virtual IReadOnlyList<IssuedCertificateStatus> MoreStatesOf(IssuedCertificateInfo row) => [];

    /// <summary>Builds the rows of the shown page again (the page's <see cref="MoreStatesOf"/> changed); the selection stays.</summary>
    protected void RefreshRows()
    {
        var keep = _selected.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        var now = Time.GetUtcNow().UtcDateTime;
        Rows.ReplaceAll([.. Rows.Select(r => Row(r.Source, now))]);
        SetSelection(Rows.Where(r => keep.Contains(r.Key)));
        GroupsChanged?.Invoke(this, EventArgs.Empty);
        SelectionRestored?.Invoke(this, EventArgs.Empty);
    }

    private IssuedCertificateRow Row(IssuedCertificateInfo source, DateTime nowUtc) => new(source)
    {
        NowUtc = nowUtc,
        MoreStates = MoreStatesOf(source),
        GroupTitle = IsGrouped ? GroupTitleOf(source) : null,
    };

    /// <summary>The header of a row's group: the type by its kind, or its issuer.</summary>
    private string? GroupTitleOf(IssuedCertificateInfo row)
    {
        var key = GroupBy.Value == IssuedCertificateGrouping.Issuer ? row.IssuerId ?? "other" : IssuedCertificateGroups.OfKind(row.Kind);
        foreach (var group in _groups)
        {
            if (group.Key == key)
            {
                return IssuedCertificateTexts.GroupHeader(group.Title, group.Count);
            }
        }

        return null;
    }

    /// <summary>The clock of the search delay and the certificate window.</summary>
    protected TimeProvider Time { get; }

    /// <summary>The certificate window (the view).</summary>
    public IIssuedCertificateListDialogs? Dialogs { get; set; }

    /// <summary>Wait after typing in the search before the server is asked.</summary>
    public TimeSpan SearchDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Rows of one page.</summary>
    public int PageSize { get; init; } = IssuedCertificateQuery.MaxLimit;

    /// <summary>The rows of the shown page, newest first (bound to the grid).</summary>
    public Collections.RangeObservableCollection<IssuedCertificateRow> Rows { get; } = [];

    /// <summary>
    /// "All states" and every state a row can have: Valid, Expires soon, Expired, Revoked (decision 2026-10-25: no Replaced,
    /// the list holds only each camera's current certificates).
    /// </summary>
    public IReadOnlyList<IssuedCertificateFilter> StateChoices { get; } =
    [
        new(string.Empty, "All states"),
        .. Enum.GetValues<IssuedCertificateState>().Where(s => s != IssuedCertificateState.Replaced)
            .Select(s => new IssuedCertificateFilter(s.ToString(), IssuedCertificateTexts.State(s))),
    ];

    /// <summary>"All uses" and every use.</summary>
    public IReadOnlyList<IssuedCertificateFilter> UseChoices { get; } =
    [
        new(string.Empty, "All uses"),
        .. ListedCertificateUses.All.Select(p => new IssuedCertificateFilter(p, ListedCertificateUses.Text(p))),
    ];

    /// <summary>"Issued by any" and the CAs the server lists, or Task and EST when it lists none.</summary>
    public Collections.RangeObservableCollection<IssuedCertificateFilter> SourceChoices { get; } = [];

    private static IReadOnlyList<IssuedCertificateFilter> SourceChoicesOfTasks { get; } =
    [
        new(string.Empty, "Issued by any"),
        .. IssuedCertificateSources.All.Select(s => new IssuedCertificateFilter(s, s)),
    ];

    /// <summary>The chosen state; a change reads the first page again.</summary>
    [ObservableProperty]
    public partial IssuedCertificateFilter StateFilter { get; set; }

    /// <summary>The chosen use; a change reads the first page again.</summary>
    [ObservableProperty]
    public partial IssuedCertificateFilter UseFilter { get; set; }

    /// <summary>The chosen source; a change reads the first page again.</summary>
    [ObservableProperty]
    public partial IssuedCertificateFilter SourceFilter { get; set; }

    /// <summary>Text in any column, the device's host name or serial, or the certificate's serial; read 300 ms after the last key.</summary>
    [ObservableProperty]
    public partial string? Search { get; set; }

    /// <summary>How many certificates match the filters and the search.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText), nameof(EmptyText), nameof(HasPages))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int Total { get; set; }

    /// <summary>The first row of the shown page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int Offset { get; set; }

    /// <summary>The first read (the overlay over the grid).</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>A later read (filter, page, change on the server): the progress row above the grid.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsProgress), nameof(ProgressText))]
    public partial bool IsReloading { get; set; }

    /// <summary>
    /// The text of a write the page runs on the list ("Revoking the certificates"); null while none runs. The progress row
    /// shows it above the grid.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking), nameof(ShowsProgress), nameof(ProgressText))]
    public partial string? WorkText { get; protected set; }

    /// <summary>A write of the page runs (<see cref="WorkText"/>): its buttons stay off.</summary>
    public bool IsWorking => WorkText is not null;

    /// <summary>The progress row above the grid shows: a later read or a write of the page.</summary>
    public bool ShowsProgress => IsReloading || IsWorking;

    /// <summary>The text of the progress row: the write while one runs, else "Reading the certificates".</summary>
    public string ProgressText => WorkText ?? IssuedCertificateTexts.Reading;

    /// <summary>"Cannot read the certificates: ..." directly below the table; null after a good read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    /// <summary><see cref="Error"/> is set.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>"1-500 of 12,345"; empty without rows.</summary>
    public string PageText => Total == 0 ? string.Empty
        : string.Create(CultureInfo.CurrentCulture, $"{Offset + 1:N0}-{Math.Min(Offset + PageSize, Total):N0} of {Total:N0}");

    /// <summary>More rows than one page: Previous and Next show.</summary>
    public bool HasPages => Total > PageSize;

    /// <summary>"Previous 500".</summary>
    public string PreviousText => string.Create(CultureInfo.CurrentCulture, $"Previous {PageSize:N0}");

    /// <summary>"Next 500".</summary>
    public string NextText => string.Create(CultureInfo.CurrentCulture, $"Next {PageSize:N0}");

    /// <summary>One line when the list is empty: what to do first, or that nothing matches.</summary>
    public string? EmptyText => Total > 0 || !_loadedOnce ? null
        : IsFiltered ? IssuedCertificateTexts.NoMatch
        : IssuedCertificateTexts.NoCertificates;

    private bool IsFiltered => StateFilter.Value.Length > 0 || UseFilter.Value.Length > 0 || SourceFilter.Value.Length > 0 || !string.IsNullOrWhiteSpace(Search);

    /// <summary>The chosen rows (the grid's selection).</summary>
    public IReadOnlyList<IssuedCertificateRow> Selected => _selected;

    /// <summary>The rows were replaced and the selection kept: the view selects them again in the grid.</summary>
    public event EventHandler? SelectionRestored;

    /// <summary>The grid's selection changed (one step for many rows).</summary>
    public void SetSelection(IEnumerable<IssuedCertificateRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _selected.Clear();
        _selected.AddRange(rows);
        ViewCertificateCommand.NotifyCanExecuteChanged();
        OnSelectionChanged();
    }

    /// <summary>After every change of <see cref="WorkText"/> (commands of a derived list stay off while a write runs).</summary>
    protected virtual void OnWorkChanged()
    {
    }

    partial void OnWorkTextChanged(string? value) => OnWorkChanged();

    /// <summary>After every change of <see cref="Selected"/> (commands of a derived list).</summary>
    protected virtual void OnSelectionChanged()
    {
    }

    /// <summary>Reads the page at <see cref="Offset"/> with the filters (the first time with the overlay).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        _started = true;
        var version = Interlocked.Increment(ref _readVersion);
        if (_loadedOnce)
        {
            IsReloading = true;
        }
        else
        {
            IsLoading = true;
        }

        try
        {
            var query = new IssuedCertificateQuery
            {
                State = Enum.TryParse<IssuedCertificateState>(StateFilter.Value, out var state) ? state : null,
                Purpose = UseFilter.Value.Length > 0 ? UseFilter.Value : null,
                Source = SourceFilter.Value.Length > 0 && !SourceFilter.IsIssuer ? SourceFilter.Value : null,
                IssuedBy = SourceFilter.Value.Length > 0 && SourceFilter.IsIssuer ? SourceFilter.Value : null,
                Search = string.IsNullOrWhiteSpace(Search) ? null : Search.Trim(),
                GroupBy = CanGroup ? GroupBy.Value : IssuedCertificateGrouping.None,
                Offset = Offset,
                Limit = PageSize,
            };
            var page = await _source.ReadAsync(query, ct).ConfigureAwait(true);
            if (version != Volatile.Read(ref _readVersion))
            {
                return; // a newer read (another filter) answers
            }

            var keep = _selected.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
            _groups = CanGroup && page.Groups is { Count: > 0 } groups ? groups : [];
            IsGrouped = _groups.Count > 0;
            var now = Time.GetUtcNow().UtcDateTime;
            Rows.ReplaceAll(page.Rows.Select(r => Row(r, now)));
            var titles = IsGrouped ? Rows.Select(r => r.GroupTitle ?? string.Empty).Distinct(StringComparer.Ordinal).ToList() : [];
            var regroup = !titles.SequenceEqual(GroupTitles, StringComparer.Ordinal);
            GroupTitles = titles;
            _loadedOnce = true;
            Offset = page.Offset;
            Total = page.Total;
            _warningDays = page.WarningDays;
            ApplyIssuers(page.Issuers);
            Error = null;
            SetSelection(Rows.Where(r => keep.Contains(r.Key)));
            OnPropertyChanged(nameof(EmptyText));
            if (regroup)
            {
                GroupsChanged?.Invoke(this, EventArgs.Empty);
            }

            SelectionRestored?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Shown directly below the table.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (version == Volatile.Read(ref _readVersion))
            {
                Error = "Cannot read the certificates: " + ServerErrors.Message(ex);
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _readVersion))
            {
                IsLoading = false;
                IsReloading = false;
            }
        }
    }

    /// <summary>The CAs of the "Issued by" filter from the page (only when they changed; the choice is kept while it is listed).</summary>
    private void ApplyIssuers(IReadOnlyList<CertificateIssuerChoice>? issuers)
    {
        if (issuers is null)
        {
            return;
        }

        IReadOnlyList<IssuedCertificateFilter> wanted = [new(string.Empty, "Issued by any"), .. issuers.Select(i => new IssuedCertificateFilter(i.Id, i.Name, IsIssuer: true))];
        if (SourceChoices.SequenceEqual(wanted))
        {
            return;
        }

        var chosen = SourceFilter;
        _applyingChoices = true;
        try
        {
            SourceChoices.ReplaceAll(wanted);
            SourceFilter = wanted.FirstOrDefault(c => c == chosen) ?? wanted[0];
        }
        finally
        {
            _applyingChoices = false;
        }

        if (SourceFilter != chosen)
        {
            Refilter(); // the chosen CA left the list
        }
    }

    partial void OnStateFilterChanged(IssuedCertificateFilter value) => Refilter();

    partial void OnUseFilterChanged(IssuedCertificateFilter value) => Refilter();

    partial void OnSourceFilterChanged(IssuedCertificateFilter value) => Refilter();

    partial void OnSearchChanged(string? value)
    {
        _searchDelay?.Cancel();
        _searchDelay?.Dispose();
        var delay = _searchDelay = new CancellationTokenSource();
        _ = SearchLaterAsync(delay.Token);
    }

    private async Task SearchLaterAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(SearchDelay, Time, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return; // typed again
        }

        Refilter();
    }

    private void Refilter()
    {
        if (!_started || _applyingChoices || StateFilter is null || UseFilter is null || SourceFilter is null || GroupBy is null)
        {
            return; // not shown yet (or the constructor sets them one by one): the first read takes the filters
        }

        Offset = 0;
        _ = LoadAsync();
    }

    private bool CanPrevious() => Offset > 0;

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private Task PreviousPageAsync()
    {
        Offset = Math.Max(0, Offset - PageSize);
        return LoadAsync();
    }

    private bool CanNext() => Offset + PageSize < Total;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextPageAsync()
    {
        Offset += PageSize;
        return LoadAsync();
    }

    private bool CanView() => _selected.Count == 1;

    /// <summary>View certificate (toolbar, row menu, double click): the shared certificate window.</summary>
    [RelayCommand(CanExecute = nameof(CanView))]
    private Task ViewCertificateAsync() => _selected.Count == 1 ? ViewAsync(_selected[0]) : Task.CompletedTask;

    /// <summary>View (a row button): the certificate of this row in the shared certificate window.</summary>
    [RelayCommand]
    private Task ViewRowAsync(IssuedCertificateRow? row) => row is null ? Task.CompletedTask : ViewAsync(row);

    /// <summary>Shows <paramref name="row"/> in the shared certificate window (or why it cannot be shown).</summary>
    protected async Task ViewAsync(IssuedCertificateRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        try
        {
            var reply = await _source.GetCertificateAsync(row.Key, CancellationToken.None).ConfigureAwait(true);
            if (reply.Certificate is not { } certificate)
            {
                await ShowMessageAsync("View certificate", reply.Error ?? IssuedCertificateTexts.NotListed).ConfigureAwait(true);
                return;
            }

            // The revocation and "ending soon" as the list says, so the window and the row's state agree.
            var now = Time.GetUtcNow().UtcDateTime;
            var details = new CertificateDetails(certificate, $"Certificate · {row.Name} · {row.Use}", now, reply.Revocation ?? row.Source.Revocation, WarningDaysFor(row, now))
            {
                UsedBy = row.UsedByTip,
            };
            if (Dialogs is { } dialogs)
            {
                await dialogs.ShowCertificateAsync(details).ConfigureAwait(true);
            }
        }
#pragma warning disable CA1031 // Shown to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            await ShowMessageAsync("View certificate", ServerErrors.Message(ex)).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// The warning days of the certificate window: the server's (PKI's setting) when the page said, else what makes the window
    /// agree with the row's state (Expires soon or Valid), else the default.
    /// </summary>
    public int WarningDaysFor(IssuedCertificateRow row, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (_warningDays is { } days)
        {
            return days;
        }

        var left = (int)Math.Floor((row.Source.NotAfterUtc - nowUtc).TotalDays);
        return row.Source.State switch
        {
            IssuedCertificateState.ExpiresSoon => Math.Max(left, 0),
            IssuedCertificateState.Valid => Math.Min(IssuedCertificateTexts.DefaultWarningDays, Math.Max(left - 1, 0)),
            _ => IssuedCertificateTexts.DefaultWarningDays,
        };
    }

    /// <summary>A message to the user (the page's message window).</summary>
    protected virtual Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
}
