using System.Globalization;
using System.Text.Json.Serialization;

using Oadm.Sdk.Certificates;

namespace Oadm.Plugins.Pki.Policies;

// PKI's policies (docs/spec/pki.md "Policies"): the page contract and the stored form, shared by the server part, the page
// (Oadm.Plugins.Pki.Client references this assembly) and the 802.1X server inside PKI. Only the BCL and the SDK. JSON: PkiJson.

/// <summary>The groups of the Policies card's filter.</summary>
public static class PolicyGroups
{
    public const string All = "all";
    public const string Issuing = "issuing";
    public const string Certificates = "certificates";
    public const string Dot1x = "dot1x";
    public const string Est = "est";
    public const string Revoke = "revoke";

    /// <summary>The filter chips of this stage, in order: only groups that have kinds (Issuing and EST come with their kinds).</summary>
    public static IReadOnlyList<string> Shown { get; } = [All, Certificates, Dot1x, Revoke];

    /// <summary>"All", "Issuing", "Certificates", "802.1X", "EST", "Revoke".</summary>
    public static string Label(string group) => group switch
    {
        All => "All",
        Issuing => "Issuing",
        Certificates => "Certificates",
        Dot1x => "802.1X",
        Est => "EST",
        Revoke => "Revoke",
        _ => group,
    };
}

/// <summary>The kinds of policies (docs/spec/pki.md "Policies", the kinds of stage 3).</summary>
public static class PolicyKinds
{
    /// <summary>The 802.1X server's TLS settings, largest EAP packet and reauthentication (one only).</summary>
    public const string Dot1xServer = "dot1xServer";

    /// <summary>Certificates of a CA may pass the 802.1X server.</summary>
    public const string AllowCa = "allowCa";

    /// <summary>The devices get a VLAN from the 802.1X server.</summary>
    public const string Vlan = "vlan";

    /// <summary>The 802.1X server refuses the devices; nothing is revoked.</summary>
    public const string Block = "block";

    /// <summary>The IEEE 802.1X certificates of the devices are revoked (on save and every new one while it exists).</summary>
    public const string Revoke = "revoke";

    /// <summary>The certificates of the devices count as "expires soon" this many days before their end.</summary>
    public const string ExpiryWarning = "expiryWarning";

    /// <summary>Every kind in the order of the editor's choice and the card (by group).</summary>
    public static IReadOnlyList<string> All { get; } = [ExpiryWarning, Dot1xServer, AllowCa, Vlan, Block, Revoke];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);

    /// <summary>"Expiry warning", "802.1X server", "Allow CA", "VLAN", "Block", "Revoke".</summary>
    public static string Name(string kind) => kind switch
    {
        Dot1xServer => "802.1X server",
        AllowCa => "Allow CA",
        Vlan => "VLAN",
        Block => "Block",
        Revoke => "Revoke",
        ExpiryWarning => "Expiry warning",
        _ => kind,
    };

    /// <summary>The group of the filter.</summary>
    public static string GroupOf(string kind) => kind switch
    {
        Dot1xServer or AllowCa or Vlan or Block => PolicyGroups.Dot1x,
        Revoke => PolicyGroups.Revoke,
        ExpiryWarning => PolicyGroups.Certificates,
        _ => PolicyGroups.All,
    };

    /// <summary>The kind chooses devices (the shared device selector).</summary>
    public static bool HasDevices(string kind) => kind is Vlan or Block or Revoke or ExpiryWarning;

    /// <summary>
    /// The kind may hold "All devices" (<see cref="PolicyDevices.AllDevices"/>): every device, also of sites added later and,
    /// for the 802.1X kinds, devices OADM does not manage. Never Revoke (like tags: it would revoke every device's certificate).
    /// </summary>
    public static bool AllowsAllDevices(string kind) => HasDevices(kind) && kind != Revoke;

    /// <summary>At most one policy of the kind.</summary>
    public static bool OneOnly(string kind) => kind == Dot1xServer;

    /// <summary>The devices are chosen by device and site only, never by tag (Revoke: a tag could revoke every device that gets it later).</summary>
    public static bool NoTags(string kind) => kind == Revoke;

    /// <summary>A kind of the 802.1X server (its page counts them: "6 policies apply").</summary>
    public static bool IsDot1x(string kind) => GroupOf(kind) == PolicyGroups.Dot1x || kind == Revoke;

    /// <summary>What the kind does, one short line in the editor.</summary>
    public static string? Description(string kind) => kind switch
    {
        Dot1xServer => "TLS settings of the 802.1X server.",
        AllowCa => "Certificates of this CA may pass the 802.1X server.",
        Vlan => "The 802.1X server puts these devices in this VLAN.",
        Block => "The 802.1X server refuses these devices, also with a valid certificate. Nothing is revoked.",
        Revoke => "Revokes the IEEE 802.1X certificates of these devices, also new ones while the policy exists.",
        ExpiryWarning => "Certificates of these devices show Expires soon this many days before their end.",
        _ => null,
    };
}

/// <summary>Defaults and limits of the policies.</summary>
public static class PolicyLimits
{
    public const int MaxPolicies = 200;
    public const int MaxNameLength = 64;
    public const int DefaultExpiryDays = 30;
    public const int MinExpiryDays = 1;
    public const int MaxExpiryDays = 365;

    /// <summary>Reauthentication every N minutes: 10 minutes to one day (30,000 devices reauthenticating every few minutes would load the switches and the server).</summary>
    public const int MinReauthMinutes = 10;
    public const int MaxReauthMinutes = 1440;

    /// <summary>The name of the default policies that cover every device ("All devices").</summary>
    public const string AllDevicesName = "All devices";

    /// <summary>The name of the default 802.1X server policy.</summary>
    public const string ServerPolicyName = "802.1X server";
}

/// <summary>
/// The devices of a policy: <see cref="AllDevices"/>, or chosen like a device group (the shared device picker's group mode):
/// every device without a site (<see cref="ThisServer"/>), the devices of <see cref="SiteIds"/>, the devices with one of
/// <see cref="Tags"/> (by name), single devices by serial, minus <see cref="ExceptSerials"/>.
/// </summary>
public sealed record PolicyDevices
{
    /// <summary>The choice "All devices".</summary>
    public static PolicyDevices Everything { get; } = new() { AllDevices = true };

    /// <summary>
    /// Every device: also the devices of sites added later and, for the 802.1X kinds, devices OADM does not manage (a
    /// certificate of an Allow CA for any device). The other fields are empty. Overlaps every other policy of its kind.
    /// </summary>
    public bool AllDevices { get; init; }

    public bool ThisServer { get; init; }

    public IReadOnlyList<Guid> SiteIds { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Serial numbers (12 hex digits, upper case).</summary>
    public IReadOnlyList<string> Serials { get; init; } = [];

    public IReadOnlyList<string> ExceptSerials { get; init; } = [];

    /// <summary>No site, tag or device is chosen.</summary>
    [JsonIgnore]
    public bool IsEmpty => !AllDevices && !ThisServer && (SiteIds?.Count ?? 0) == 0 && (Tags?.Count ?? 0) == 0 && (Serials?.Count ?? 0) == 0;

    /// <summary>The same choice (order and case of tags and serials do not count).</summary>
    public bool SameAs(PolicyDevices? other) =>
        other is not null && AllDevices == other.AllDevices && ThisServer == other.ThisServer
        && (SiteIds ?? []).ToHashSet().SetEquals(other.SiteIds ?? [])
        && (Tags ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(other.Tags ?? [])
        && (Serials ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(other.Serials ?? [])
        && (ExceptSerials ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(other.ExceptSerials ?? []);
}

/// <summary>
/// One policy. The fields of its kind are set, the others null. The stored list is in age order: of two overlapping policies
/// the older is the earlier one.
/// </summary>
public sealed record PkiPolicy
{
    /// <summary>Empty for a new policy (PKI gives it an id).</summary>
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>One of <see cref="PolicyKinds"/>.</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>The devices of a kind with devices; null for the others.</summary>
    public PolicyDevices? Devices { get; init; }

    /// <summary>VLAN: 1..4094.</summary>
    public int? Vlan { get; init; }

    /// <summary>Allow CA: PKI's id of the CA (<see cref="CertificateAuthorityInfo.Id"/>; the OADM CA covers its previous ones).</summary>
    public string? CaId { get; init; }

    /// <summary>Allow CA: only certificates that name a device OADM manages pass (default); false: any valid certificate of the CA.</summary>
    public bool? OnlyManaged { get; init; }

    /// <summary>802.1X server: "1.0", "1.1", "1.2" (default) or "1.3".</summary>
    public string? MinimumTls { get; init; }

    /// <summary>802.1X server: the checked cipher suites (IANA ids).</summary>
    public IReadOnlyList<int>? CipherSuites { get; init; }

    /// <summary>802.1X server: the largest EAP packet in bytes (500..1,400, default 1,020).</summary>
    public int? LargestEapPacket { get; init; }

    /// <summary>802.1X server: reauthentication every N minutes; null = the switch decides.</summary>
    public int? ReauthMinutes { get; init; }

    /// <summary>Expiry warning: days before the end (1..365).</summary>
    public int? Days { get; init; }

    /// <summary>Set by PKI: who saved it last (the user of a revoke policy's later revocations).</summary>
    public string? SavedBy { get; init; }
}

/// <summary>One row of the Policies card: the policy with its kind's name, its group, the summary and a warning.</summary>
/// <param name="Policy">The stored policy.</param>
/// <param name="KindName">"VLAN", "Allow CA", ...</param>
/// <param name="Group">One of <see cref="PolicyGroups"/>.</param>
/// <param name="Summary">"VLAN 30", "OADM CA, only devices OADM manages", "30 days".</param>
/// <param name="Warning">An overlap a tag or site change made later ("10.0.0.48 is in Building B and PTZ. Building B applies."); null when none.</param>
public sealed record PolicyRow(PkiPolicy Policy, string KindName, string Group, string Summary, string? Warning);

/// <summary>A CA a policy can name (previous OADM CAs follow the OADM CA and are no choice of their own).</summary>
public sealed record PolicyCaChoice(string Id, string Name, CertificateAuthorityKind Kind)
{
    /// <summary>The OADM CA: how many previous OADM CAs it covers.</summary>
    public int PreviousCount { get; init; }
}

/// <summary>A cipher suite of the 802.1X server policy.</summary>
/// <param name="Id">IANA id.</param>
/// <param name="Name">IANA name.</param>
/// <param name="Legacy">An old AES-CBC SHA-1 suite: listed only for a minimum of TLS 1.0 or 1.1.</param>
/// <param name="Tls13">A TLS 1.3 suite.</param>
/// <param name="Usable">The 802.1X server's certificate can use it (its key is ECDSA: ECDHE_RSA suites are never negotiated).</param>
public sealed record PolicyCipherSuite(int Id, string Name, bool Legacy, bool Tls13, bool Usable);

/// <summary>A site of the device selector.</summary>
public sealed record PolicySite(Guid Id, string Name);

/// <summary>The answer of <see cref="PolicyMethods.GetPolicies"/>.</summary>
public sealed record PolicyList
{
    /// <summary>Every policy in age order (oldest first). A user limited to a device group: none.</summary>
    public IReadOnlyList<PolicyRow> Rows { get; init; } = [];

    /// <summary>The CAs of PKI an Allow CA policy can name.</summary>
    public IReadOnlyList<PolicyCaChoice> Cas { get; init; } = [];

    /// <summary>The cipher suites of the 802.1X server policy, in the page's order.</summary>
    public IReadOnlyList<PolicyCipherSuite> CipherSuites { get; init; } = [];

    /// <summary>The sites of the server (the device selector, the row texts).</summary>
    public IReadOnlyList<PolicySite> Sites { get; init; } = [];

    /// <summary>The tags a policy can name: those of the managed devices and of the policies.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The caller may change the policies ("Manage the certificate authority" and all devices).</summary>
    public bool MayManage { get; init; }
}

/// <summary>An input error of a policy: the field (<see cref="PolicyFields"/>) and the message below it.</summary>
public sealed record PolicyFieldError(string Field, string Message);

/// <summary>The answer of a save or delete: the list after it, or the error that refused it (nothing changed).</summary>
public sealed record PolicySaveReply
{
    public PolicyFieldError? Error { get; init; }

    public PolicyList? List { get; init; }

    /// <summary>The saved policy (its id for a new one).</summary>
    public PkiPolicy? Saved { get; init; }
}

/// <summary>The id of a policy (<see cref="PolicyMethods.DeletePolicy"/>).</summary>
public sealed record PolicyRef(Guid Id);

/// <summary>The answer of <see cref="PolicyMethods.CountRevocations"/>.</summary>
public sealed record PolicyRevocationCount(int Count);

/// <summary>The page methods of the policies (PKI's <c>InvokeAsync</c>).</summary>
public static class PolicyMethods
{
    /// <summary>() -> <see cref="PolicyList"/>. A user limited to a device group: an empty list.</summary>
    public const string GetPolicies = "getPolicies";

    /// <summary><see cref="PkiPolicy"/> -> <see cref="PolicySaveReply"/>: add (no id) or change. "Manage the certificate authority", all devices, audited.</summary>
    public const string SavePolicy = "savePolicy";

    /// <summary><see cref="PolicyRef"/> -> <see cref="PolicySaveReply"/>. "Manage the certificate authority", all devices, audited.</summary>
    public const string DeletePolicy = "deletePolicy";

    /// <summary><see cref="PolicyDevices"/> -> <see cref="PolicyRevocationCount"/>: the IEEE 802.1X certificates a revoke policy would revoke now.</summary>
    public const string CountRevocations = "countRevocations";

    /// <summary>Event (payload <c>{}</c>): the policies changed; pages read them again.</summary>
    public const string Topic = "policies";
}

/// <summary>The fields of <see cref="PolicyFieldError.Field"/> (the editor puts the message below the field).</summary>
public static class PolicyFields
{
    public const string Name = "name";
    public const string Kind = "kind";
    public const string Devices = "devices";
    public const string Vlan = "vlan";
    public const string Ca = "caId";
    public const string MinimumTls = "minimumTls";
    public const string CipherSuites = "cipherSuites";
    public const string LargestEapPacket = "largestEapPacket";
    public const string ReauthMinutes = "reauthMinutes";
    public const string Days = "days";
}

/// <summary>The texts of the policies (one phrase per concept), the same on the page and in the server's refusals.</summary>
public static class PolicyTexts
{
    public const string EnterName = "Enter a name.";
    public const string NameTooLong = "Use at most 64 characters.";
    public const string ChooseKind = "Choose a kind.";
    public const string ChooseDevices = "Choose sites, tags or devices.";
    public const string RevokeNoTags = "Revoke policies choose devices and sites, not tags.";
    public const string RevokeNoAll = "Revoke policies choose devices and sites, not all devices.";
    public const string SiteGone = "This site no longer exists.";
    public const string PolicyGone = "This policy no longer exists.";
    public const string KindKept = "A policy keeps its kind. Add a new policy instead.";
    public const string ChooseCa = "Choose a CA.";
    public const string UnknownCa = "This certificate authority is not in the PKI.";
    public const string RemovedCa = "(removed CA)";
    public const string VlanRange = "Use 1 to 4094.";
    public const string DaysRange = "Use 1 to 365 days.";
    public const string ReauthRange = "Use 10 to 1,440 minutes.";
    public const string TooMany = "Use at most 200 policies.";
    public const string ThisServer = "This server";
    public const string AllDevices = PolicyLimits.AllDevicesName;
    public const string ChosenDevices = "Chosen devices";
    public const string OnlyManaged = "Only devices OADM manages";
    public const string AnyDevice = "Any device with a valid certificate";
    public const string SwitchDecides = "Switch decides";
    public const string NotRunning = "The PKI is not running.";

    /// <summary>"Another VLAN policy has this name."</summary>
    public static string NameTaken(string kind) => $"Another {PolicyKinds.Name(kind)} policy has this name.";

    /// <summary>"Only one 802.1X server policy is allowed."</summary>
    public static string OneOnly(string kind) => $"Only one {PolicyKinds.Name(kind)} policy is allowed.";

    /// <summary>"Another Allow CA policy has this CA."</summary>
    public const string CaTaken = "Another Allow CA policy has this CA.";

    /// <summary>"10.0.0.48 is in Building B and PTZ." (refused on save; the same words as the warning of a later overlap).</summary>
    public static string Overlap(string device, int more, string first, string second) => more == 0
        ? $"{device} is in {first} and {second}."
        : string.Create(CultureInfo.CurrentCulture, $"{device} and {more:N0} other {(more == 1 ? "device are" : "devices are")} in {first} and {second}.");

    /// <summary>"All devices includes every device, also those of PTZ." (an "All devices" policy overlaps every other of its kind).</summary>
    public static string OverlapAll(string all, string other) => $"{all} includes every device, also those of {other}.";

    /// <summary>"Building B applies." after the warning of a later overlap.</summary>
    public static string Applies(string name) => $"{name} applies.";

    /// <summary>The refusal of a protected tag for a user limited to a device group.</summary>
    public static string TagRefusal(string tag) => $"The tag {tag} is used by a PKI policy. Only users with all devices can add or remove it. Nothing was changed.";

    /// <summary>The refusal of a rename or delete of a protected tag.</summary>
    public static string TagChangeRefusal(string tag) => $"The tag {tag} is used by a PKI policy. Change the policy first. Nothing was changed.";

    /// <summary>"Used by the policy OADM CA. Change or delete the policy first."</summary>
    public static string UsedByPolicy(string name) => $"Used by the policy {name}. Change or delete the policy first.";

    /// <summary>"802.1X: OADM CA, managed only" (the Used by column of the CA list).</summary>
    public static string UsedBy(PkiPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return "802.1X: " + policy.Name + (policy.OnlyManaged ?? true ? ", managed only" : string.Empty);
    }

    /// <summary>"VLAN 30 by Building B" (Sessions and Log of the 802.1X server).</summary>
    public static string VlanBy(int vlan, string? policy) =>
        string.IsNullOrEmpty(policy) ? string.Create(CultureInfo.InvariantCulture, $"VLAN {vlan}") : string.Create(CultureInfo.InvariantCulture, $"VLAN {vlan} by {policy}");

    /// <summary>"Every 60 minutes".</summary>
    public static string Reauth(int? minutes) =>
        minutes is { } m ? string.Create(CultureInfo.CurrentCulture, $"reauthenticate every {m:N0} minutes") : "switch decides reauthentication";

    /// <summary>"All devices"; "This server, site Building B, tag Lobby, 3 devices, except 1".</summary>
    public static string DevicesText(PolicyDevices? devices, IReadOnlyDictionary<Guid, string> siteNames)
    {
        ArgumentNullException.ThrowIfNull(siteNames);
        if (devices is null)
        {
            return string.Empty;
        }

        if (devices.AllDevices)
        {
            return AllDevices;
        }

        var parts = new List<string>();
        if (devices.ThisServer)
        {
            parts.Add(ThisServer);
        }

        parts.AddRange((devices.SiteIds ?? []).Select(id => "Site " + (siteNames.TryGetValue(id, out var name) ? name : "(removed)")));
        parts.AddRange((devices.Tags ?? []).Select(t => "Tag " + t));
        var serials = devices.Serials?.Count ?? 0;
        if (serials > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"{serials:N0} {(serials == 1 ? "device" : "devices")}"));
        }

        var excepts = devices.ExceptSerials?.Count ?? 0;
        if (excepts > 0)
        {
            parts.Add(string.Create(CultureInfo.CurrentCulture, $"except {excepts:N0}"));
        }

        return string.Join(", ", parts);
    }
}
