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

    /// <summary>The filter chips, in order.</summary>
    public static IReadOnlyList<string> Shown { get; } = [All, Issuing, Certificates, Dot1x, Est, Revoke];

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

/// <summary>The kinds of policies (docs/spec/pki.md "Policies", the kinds of stages 3, 4 and 6).</summary>
public static class PolicyKinds
{
    /// <summary>Issuing: the certificates of Enable HTTPS and of the EST profiles for HTTPS and RTSPS (no devices).</summary>
    public const string HttpsCertificate = "httpsCertificate";

    /// <summary>Issuing: the certificates of Enable IEEE 802.1X and of the EST profiles for IEEE 802.1X and MQTT, with the 802.1X settings (no devices).</summary>
    public const string Dot1xCertificate = "dot1xCertificate";

    /// <summary>Issuing: the certificate of the 802.1X server or of the EST server (one per server, no devices).</summary>
    public const string ServerCertificate = "serverCertificate";

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

    /// <summary>EST: the profiles the devices may use (all, chosen ones, or no enrollment). A device no such policy holds is refused.</summary>
    public const string Enrollment = "enrollment";

    /// <summary>EST: cameras may sign in with certificates of this CA, for new certificates or for renewals (no devices).</summary>
    public const string SignInCa = "signInCa";

    /// <summary>Every kind in the order of the editor's choice and the card (by group).</summary>
    public static IReadOnlyList<string> All { get; } = [HttpsCertificate, Dot1xCertificate, ServerCertificate, ExpiryWarning, Dot1xServer, AllowCa, Vlan, Block, Enrollment, SignInCa, Revoke];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);

    /// <summary>"HTTPS certificate", "Expiry warning", "802.1X server", "Allow CA", "VLAN", "Block", "Revoke".</summary>
    public static string Name(string kind) => kind switch
    {
        HttpsCertificate => "HTTPS certificate",
        Dot1xCertificate => "IEEE 802.1X certificate",
        ServerCertificate => "Server certificate",
        Dot1xServer => "802.1X server",
        AllowCa => "Allow CA",
        Vlan => "VLAN",
        Block => "Block",
        Revoke => "Revoke",
        ExpiryWarning => "Expiry warning",
        Enrollment => "Enrollment",
        SignInCa => "Sign-in CA",
        _ => kind,
    };

    /// <summary>The group of the filter.</summary>
    public static string GroupOf(string kind) => kind switch
    {
        Dot1xServer or AllowCa or Vlan or Block => PolicyGroups.Dot1x,
        Revoke => PolicyGroups.Revoke,
        ExpiryWarning => PolicyGroups.Certificates,
        HttpsCertificate or Dot1xCertificate or ServerCertificate => PolicyGroups.Issuing,
        Enrollment or SignInCa => PolicyGroups.Est,
        _ => PolicyGroups.All,
    };

    /// <summary>An issuing kind: the values a certificate is issued with (CA, valid days, key).</summary>
    public static bool IsIssuing(string? kind) => kind is HttpsCertificate or Dot1xCertificate or ServerCertificate;

    /// <summary>An issuing kind of device certificates (the task dialogs and the EST profiles pick one).</summary>
    public static bool IsDeviceIssuing(string? kind) => kind is HttpsCertificate or Dot1xCertificate;

    /// <summary>The kind chooses devices (the shared device selector).</summary>
    public static bool HasDevices(string kind) => kind is Vlan or Block or Revoke or ExpiryWarning or Enrollment;

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

    /// <summary>A kind of the EST server (its page counts them: "2 policies apply").</summary>
    public static bool IsEst(string kind) => GroupOf(kind) == PolicyGroups.Est;

    /// <summary>
    /// The kind gives its devices something (a VLAN, enrollment): without the host's protection of tags its tags match no
    /// device (fail closed), since a user limited to a device group could add the tag to a device.
    /// </summary>
    public static bool GrantsByTag(string kind) => kind is Vlan or Enrollment;

    /// <summary>What the kind does, one short line in the editor.</summary>
    public static string? Description(string kind) => kind switch
    {
        HttpsCertificate => "Certificates for HTTPS from Enable HTTPS and the EST server.",
        Dot1xCertificate => "Certificates for IEEE 802.1X from Enable IEEE 802.1X and the EST server.",
        ServerCertificate => "The certificate of the 802.1X server or the EST server.",
        Dot1xServer => "TLS settings of the 802.1X server.",
        AllowCa => "Certificates of this CA may pass the 802.1X server.",
        Vlan => "The 802.1X server puts these devices in this VLAN.",
        Block => "The 802.1X server refuses these devices, also with a valid certificate. Nothing is revoked.",
        Revoke => "Revokes the IEEE 802.1X certificates of these devices, also new ones while the policy exists.",
        ExpiryWarning => "Certificates of these devices show Expires soon this many days before their end.",
        Enrollment => "The EST profiles these devices may use. The EST server refuses devices no Enrollment policy holds.",
        SignInCa => "Cameras may sign in to the EST server with certificates of this CA.",
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

    /// <summary>Valid days of an issuing policy (the range of the former Device certificates card).</summary>
    public const int MinValidDays = 1;
    public const int MaxValidDays = 3650;
    public const int DefaultValidDays = 365;

    /// <summary>The EST server's own certificate as before the policies: 90 days.</summary>
    public const int DefaultEstServerDays = 90;

    /// <summary>A server certificate is made again this long before its end (at most a third of its validity).</summary>
    public static readonly TimeSpan ServerRenewBefore = TimeSpan.FromDays(30);

    /// <summary>The names of the default issuing policies.</summary>
    public const string HttpsName = "HTTPS";
    public const string Dot1xName = "IEEE 802.1X";
    public const string Dot1xServerCertificateName = "802.1X server";
    public const string EstServerCertificateName = "EST server";

    /// <summary>When a server certificate valid for <paramref name="validity"/> is made again: 30 days before its end, at most a third of it.</summary>
    public static TimeSpan RenewBefore(TimeSpan validity) => validity / 3 < ServerRenewBefore ? validity / 3 : ServerRenewBefore;
}

/// <summary>The key of an issuing policy.</summary>
public static class PolicyKeys
{
    public const string Ecc = "ecc";
    public const string Rsa = "rsa";

    public static IReadOnlyList<string> All { get; } = [Ecc, Rsa];

    public static bool IsKnown(string? key) => key is Ecc or Rsa;

    /// <summary>"ECC", "RSA".</summary>
    public static string Name(string? key) => key switch
    {
        Ecc => "ECC",
        Rsa => "RSA",
        _ => key ?? string.Empty,
    };
}

/// <summary>Which EST profiles an Enrollment policy lets its devices use.</summary>
public static class PolicyProfiles
{
    /// <summary>Every profile of the EST server.</summary>
    public const string All = "all";

    /// <summary>Only <see cref="PkiPolicy.Profiles"/>.</summary>
    public const string Chosen = "chosen";

    /// <summary>No enrollment: every profile is refused.</summary>
    public const string None = "none";

    public static IReadOnlyList<string> Scopes { get; } = [All, Chosen, None];

    public static bool IsKnown(string? scope) => scope is All or Chosen or None;

    /// <summary>"All profiles", "Chosen profiles", "No enrollment".</summary>
    public static string Name(string? scope) => scope switch
    {
        All => "All profiles",
        Chosen => "Chosen profiles",
        None => "No enrollment",
        _ => scope ?? string.Empty,
    };
}

/// <summary>What a Sign-in CA policy lets a camera sign in for.</summary>
public static class PolicySignIn
{
    /// <summary>New certificates, and renewals too (decision of stage 6: a camera that may get a new certificate with it may renew with it).</summary>
    public const string New = "new";

    /// <summary>Renewals only.</summary>
    public const string Renewal = "renewal";

    public static IReadOnlyList<string> All { get; } = [New, Renewal];

    public static bool IsKnown(string? value) => value is New or Renewal;

    /// <summary>"New certificates", "Renewals".</summary>
    public static string Name(string? value) => value switch
    {
        New => "New certificates",
        Renewal => "Renewals",
        _ => value ?? string.Empty,
    };
}

/// <summary>Which server a Server certificate policy is for.</summary>
public static class PolicyServers
{
    public const string Dot1x = "dot1xServer";
    public const string Est = "estServer";

    public static IReadOnlyList<string> All { get; } = [Dot1x, Est];

    public static bool IsKnown(string? server) => server is Dot1x or Est;

    /// <summary>"802.1X server", "EST server".</summary>
    public static string Name(string? server) => server switch
    {
        Dot1x => "802.1X server",
        Est => "EST server",
        _ => server ?? string.Empty,
    };
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

    /// <summary>
    /// Allow CA: PKI's id of the CA (<see cref="CertificateAuthorityInfo.Id"/>; the OADM CA covers its previous ones). Issuing:
    /// the CA that issues (only the OADM CA issues today).
    /// </summary>
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

    /// <summary>Issuing: how long a certificate is valid, in days (1..3,650; never past the CA's end).</summary>
    public int? ValidDays { get; init; }

    /// <summary>Issuing: the key (<see cref="PolicyKeys"/>) the device or the server makes.</summary>
    public string? Key { get; init; }

    /// <summary>IEEE 802.1X certificate: EAPOL version 1 or 2.</summary>
    public int? EapolVersion { get; init; }

    /// <summary>IEEE 802.1X certificate: the EAP identity (<c>Dot1xIdentity</c>: mac, hostName, custom).</summary>
    public string? Identity { get; init; }

    /// <summary>IEEE 802.1X certificate: the custom identity with {serial} and {hostName}.</summary>
    public string? CustomIdentity { get; init; }

    /// <summary>IEEE 802.1X certificate: the CA the devices check the RADIUS server with (<c>RadiusCaSource</c>: oadm, imported).</summary>
    public string? RadiusCa { get; init; }

    /// <summary>IEEE 802.1X certificate: the imported RADIUS server CA (PEM, public only).</summary>
    public string? RadiusCaPem { get; init; }

    /// <summary>Server certificate: the server it is for (<see cref="PolicyServers"/>).</summary>
    public string? Server { get; init; }

    /// <summary>Enrollment: which profiles the devices may use (<see cref="PolicyProfiles"/>).</summary>
    public string? ProfileScope { get; init; }

    /// <summary>Enrollment with <see cref="PolicyProfiles.Chosen"/>: the names of the EST profiles (as the cameras use them in their URL).</summary>
    public IReadOnlyList<string>? Profiles { get; init; }

    /// <summary>Sign-in CA: new certificates or renewals (<see cref="PolicySignIn"/>).</summary>
    public string? SignInFor { get; init; }

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

    /// <summary>The CAs a Sign-in CA policy can name: the Axis device ID, the OADM CA and the previous OADM CAs.</summary>
    public IReadOnlyList<PolicyCaChoice> SignInCas { get; init; } = [];

    /// <summary>The names of the EST server's profiles (an Enrollment policy chooses among them).</summary>
    public IReadOnlyList<string> EstProfiles { get; init; } = [];

    /// <summary>The cipher suites of the 802.1X server policy, in the page's order.</summary>
    public IReadOnlyList<PolicyCipherSuite> CipherSuites { get; init; } = [];

    /// <summary>The sites of the server (the device selector, the row texts).</summary>
    public IReadOnlyList<PolicySite> Sites { get; init; } = [];

    /// <summary>The tags a policy can name: those of the managed devices and of the policies.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>The caller may change the policies ("Manage the certificate authority" and all devices).</summary>
    public bool MayManage { get; init; }

    /// <summary>Why the list is empty although policies are stored (<see cref="PolicyTexts.Unreadable"/>); null when it was read.</summary>
    public string? Error { get; init; }
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
    public const string ValidDays = "validDays";
    public const string Key = "key";
    public const string EapolVersion = "eapolVersion";
    public const string Identity = "identity";
    public const string CustomIdentity = "customIdentity";
    public const string RadiusCa = "radiusCa";
    public const string Server = "server";
    public const string ProfileScope = "profileScope";
    public const string Profiles = "profiles";
    public const string SignInFor = "signInFor";
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
    public const string Unreadable = "The policies cannot be read. Restore a backup.";
    public const string ValidDaysRange = "Use 1 to 3,650 days.";
    public const string ChooseKey = "Choose a key.";
    public const string OnlyOadmIssues = "Only the OADM CA issues certificates.";
    public const string ChooseServer = "Choose a server.";
    public const string ServerKept = "A Server certificate policy keeps its server.";
    public const string Dot1xServerNeedsEcc = "The 802.1X server uses an ECC key.";
    public const string RadiusCaUnreadable = "The CA certificate of the RADIUS server cannot be read. Import it again.";
    public const string ChooseProfileScope = "Choose which profiles.";
    public const string ChooseProfiles = "Choose at least one profile.";
    public const string ChooseSignInFor = "Choose new certificates or renewals.";
    public const string NoSignInCa = "Cameras sign in with the Axis device ID or an OADM CA.";

    /// <summary>"The EST server has no profile web." (an Enrollment policy names a profile the EST server does not have).</summary>
    public static string UnknownProfile(string profile) => $"The EST server has no profile {profile}.";

    /// <summary>The row's summary of an Enrollment policy: "All profiles", "Profile https", "Profiles https and dot1x", "No enrollment".</summary>
    public static string EnrollmentSummary(PkiPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.ProfileScope != PolicyProfiles.Chosen)
        {
            return PolicyProfiles.Name(policy.ProfileScope);
        }

        var profiles = policy.Profiles ?? [];
        return profiles.Count switch
        {
            0 => PolicyProfiles.Name(PolicyProfiles.None),
            1 => "Profile " + profiles[0],
            _ => "Profiles " + string.Join(", ", profiles.Take(profiles.Count - 1)) + " and " + profiles[^1],
        };
    }

    /// <summary>"for new certificates", "for renewals" (the row's summary of a Sign-in CA policy).</summary>
    public static string SignInForText(string? value) => value == PolicySignIn.Renewal ? "for renewals" : "for new certificates";

    /// <summary>"Another Sign-in CA policy has this CA."</summary>
    public const string SignInCaTaken = "Another Sign-in CA policy has this CA.";

    /// <summary>"Another Server certificate policy is for the EST server."</summary>
    public static string ServerTaken(string? server) => $"Another Server certificate policy is for the {PolicyServers.Name(server)}.";

    /// <summary>"The EST server gets its certificate from this policy. Change it instead." (a Server certificate policy is never deleted).</summary>
    public static string ServerNeedsPolicy(string? server) => $"The {PolicyServers.Name(server)} gets its certificate from this policy. Change it instead.";

    /// <summary>The refusal to delete the last policy of a device issuing kind (the task needs one).</summary>
    public static string LastIssuing(string kind) => kind == PolicyKinds.Dot1xCertificate
        ? "Enable IEEE 802.1X needs an IEEE 802.1X certificate policy. Add another one first."
        : "Enable HTTPS needs an HTTPS certificate policy. Add another one first.";

    /// <summary>"Used by the EST profile https." / "Used by the EST profiles https and dot1x."</summary>
    public static string UsedByProfiles(IReadOnlyList<string> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles.Count == 1
            ? $"Used by the EST profile {profiles[0]}."
            : $"Used by the EST profiles {string.Join(", ", profiles.Take(profiles.Count - 1))} and {profiles[^1]}.";
    }

    /// <summary>"Contoso RADIUS CA, valid until 2030-01-01" (the imported RADIUS server CA).</summary>
    public static string RadiusCaLine(string commonName, DateTime notAfterUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"{commonName}, valid until {notAfterUtc:yyyy-MM-dd}");

    /// <summary>"365 days".</summary>
    public static string DaysText(int? days) => string.Create(CultureInfo.CurrentCulture, $"{days:N0} {(days == 1 ? "day" : "days")}");

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

    /// <summary>"only devices OADM manages" or "any device with a valid certificate": <see cref="OnlyManaged"/> or <see cref="AnyDevice"/> inside a sentence.</summary>
    public static string AllowCaScope(bool onlyManaged)
    {
        var text = onlyManaged ? OnlyManaged : AnyDevice;
        return char.ToLowerInvariant(text[0]) + text[1..];
    }

    /// <summary>"802.1X: OADM CA (only devices OADM manages)" (the Used by column of the CA list; its entries are joined with commas).</summary>
    public static string UsedBy(PkiPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return "802.1X: " + policy.Name + (policy.OnlyManaged ?? true ? " (" + AllowCaScope(true) + ")" : string.Empty);
    }

    /// <summary>"VLAN 30 by Building B" (Sessions and Log of the 802.1X server).</summary>
    public static string VlanBy(int vlan, string? policy) =>
        string.IsNullOrEmpty(policy) ? string.Create(CultureInfo.InvariantCulture, $"VLAN {vlan}") : string.Create(CultureInfo.InvariantCulture, $"VLAN {vlan} by {policy}");

    /// <summary>"reauthenticate every 60 minutes", "switch decides reauthentication" (the row's summary).</summary>
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
