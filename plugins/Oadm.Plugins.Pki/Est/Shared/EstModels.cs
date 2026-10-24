using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Sdk.Certificates;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.Est;

/// <summary>Ids and names shared by the server part, the page and the tests.</summary>
public static class EstPluginInfo
{
    public const string PluginId = "oadm.est";
    public const string DisplayName = "EST server";
    public const string IconKey = "lock";

    /// <summary>The default port (TCP; above 1024, so no rights are needed).</summary>
    public const int DefaultPort = 9454;

    /// <summary>The path of every EST operation: <c>/.well-known/est/&lt;profile&gt;/&lt;operation&gt;</c>.</summary>
    public const string PathPrefix = "/.well-known/est/";

    /// <summary>Enrollments kept by default (newest first).</summary>
    public const int DefaultEnrollmentsKept = 500;

    /// <summary>The most certificates the Not renewed list shows.</summary>
    public const int CertificatesPageSize = 500;

    /// <summary>Event "Certificate enrolled" (device, profile, new or renewal).</summary>
    public const string EnrolledEvent = "oadm.est.enrolled";

    /// <summary>Event "Enrollment refused" (device, profile, reason).</summary>
    public const string RefusedEvent = "oadm.est.refused";

    /// <summary>Event "Certificate not renewed" (device, profile, valid until).</summary>
    public const string NotRenewedEvent = "oadm.est.notrenewed";
}

/// <summary>Page backend methods (<c>InvokeAsync</c>) and live event topics.</summary>
public static class EstMethods
{
    /// <summary>() -> <see cref="EstState"/>.</summary>
    public const string GetState = "getState";

    /// <summary>() -> <see cref="EnrollmentList"/>.</summary>
    public const string GetEnrollments = "getEnrollments";

    /// <summary>No payload -> <see cref="CertificateList"/> of the certificates the cameras did not renew in time.</summary>
    public const string GetNotRenewed = "getNotRenewed";

    /// <summary><see cref="SaveRequest"/> -> <see cref="SaveReply"/> (Admin).</summary>
    public const string Save = "save";

    /// <summary>() -> (): deletes every enrollment (Admin).</summary>
    public const string ClearEnrollments = "clearEnrollments";

    /// <summary><see cref="CertificateQuery"/> -> <see cref="CertificateDescription"/> of a certificate an enrollment issued.</summary>
    public const string GetCertificate = "getCertificate";

    /// <summary>The certificate of an enrollment that is no longer listed (or of an older OADM, which kept none).</summary>
    public const string CertificateGone = "OADM no longer keeps this certificate.";

    /// <summary>Event: <see cref="EstState"/> changed (status, a relay, the settings).</summary>
    public const string StateTopic = "state";

    /// <summary>Event: new enrollments (<see cref="EnrollmentList"/> of the new ones).</summary>
    public const string EnrollmentsTopic = "enrollments";

    /// <summary>Event: every enrollment was deleted; the pages empty their list.</summary>
    public const string EnrollmentsClearedTopic = "enrollmentsCleared";

    /// <summary>Event: certificates were flagged Not renewed; the page reads the list again.</summary>
    public const string CertificatesTopic = "certificates";
}

/// <summary>What a profile's certificates are for on the camera.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProfileUse>))]
public enum ProfileUse
{
    Https = 0,
    Dot1x = 1,
    Mqtt = 2,
    Rtsps = 3,
}

/// <summary>The key the camera creates (and the device ID certificate it signs in with).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProfileKey>))]
public enum ProfileKey
{
    Rsa = 0,
    Ecc = 1,
}

/// <summary>An EST profile: one path, one kind of certificate, issued with one issuing policy of PKI.</summary>
/// <param name="Name">The path segment and name ("https"): letters, digits and '_', 1..32 characters (the camera refuses '-' in its profile ids).</param>
/// <param name="Use">What the camera uses the certificate for.</param>
/// <param name="ValidityDays">Before the issuing policies (OADM 4.1): how long each certificate was valid. Read only by the conversion.</param>
/// <param name="Key">Before the issuing policies: the key the camera created. Read only by the conversion.</param>
/// <param name="Enabled">Off: the path answers like an unknown one.</param>
public sealed record EstProfile(string Name, ProfileUse Use, int ValidityDays = 365, ProfileKey Key = ProfileKey.Ecc, bool Enabled = true)
{
    /// <summary>
    /// The issuing policy of PKI the certificates of this profile are issued with (its valid days and key; docs/spec/pki.md):
    /// an HTTPS certificate policy for HTTPS and RTSPS, an IEEE 802.1X certificate policy for IEEE 802.1X and MQTT.
    /// </summary>
    public Guid? PolicyId { get; init; }

    /// <summary>The kind of issuing policy the profile's use takes (<c>PolicyKinds</c>).</summary>
    public static string PolicyKindOf(ProfileUse use) => use is ProfileUse.Dot1x or ProfileUse.Mqtt
        ? Oadm.Plugins.Pki.Policies.PolicyKinds.Dot1xCertificate
        : Oadm.Plugins.Pki.Policies.PolicyKinds.HttpsCertificate;

    /// <summary>Server certificates for HTTPS and RTSPS, client certificates for IEEE 802.1X and MQTT.</summary>
    [JsonIgnore]
    public DeviceCertificateUse CertificateUse => Use is ProfileUse.Dot1x or ProfileUse.Mqtt ? DeviceCertificateUse.Client : DeviceCertificateUse.Server;

    /// <summary>The use in PKI's list of issued certificates (<see cref="DeviceCertificatePurposes"/>).</summary>
    [JsonIgnore]
    public string Purpose => Use switch
    {
        ProfileUse.Dot1x => DeviceCertificatePurposes.Dot1x,
        ProfileUse.Mqtt => DeviceCertificatePurposes.Mqtt,
        ProfileUse.Rtsps => DeviceCertificatePurposes.Rtsps,
        _ => DeviceCertificatePurposes.Https,
    };
}

/// <summary>Plugin setting <c>config</c>.</summary>
public sealed record EstConfig
{
    public int Port { get; init; } = EstPluginInfo.DefaultPort;

    /// <summary>The interface it listens on (<see cref="InterfaceOptions.AllId"/> = every address).</summary>
    public string InterfaceId { get; init; } = InterfaceOptions.AllId;

    /// <summary>Its name when saved ("Ethernet"), shown while it is gone.</summary>
    public string? InterfaceName { get; init; } = InterfaceOptions.AllLabel;

    public IReadOnlyList<EstProfile> Profiles { get; init; } = Defaults;

    /// <summary>Enrollments kept (newest first), 100..5000.</summary>
    public int EnrollmentsKept { get; init; } = EstPluginInfo.DefaultEnrollmentsKept;

    /// <summary>
    /// Before the Sign-in CA policies (stage 1 of docs/spec/pki.md): the ids of PKI's CAs a camera could sign in with.
    /// Default: the Axis device ID and the OADM CA (renewals). Read only by the conversion into Sign-in CA policies; a save of
    /// the page keeps the stored value.
    /// </summary>
    public IReadOnlyList<string> SignInWith { get; init; } = DefaultSignInWith;

    public static IReadOnlyList<string> DefaultSignInWith { get; } =
        [Oadm.Sdk.Certificates.CertificateAuthorityInfo.AxisDeviceIdId, Oadm.Sdk.Certificates.CertificateAuthorityInfo.OadmId];

    /// <summary>The two ready-made profiles.</summary>
    public static IReadOnlyList<EstProfile> Defaults { get; } =
    [
        new("https", ProfileUse.Https),
        new("dot1x", ProfileUse.Dot1x),
    ];
}

/// <summary>New certificate or a renewal.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EnrollmentKind>))]
public enum EnrollmentKind
{
    New = 0,
    Renewal = 1,
}

/// <summary>One enrollment request as the page lists it.</summary>
public sealed record Enrollment
{
    public DateTime TimeUtc { get; init; }

    public Guid? DeviceId { get; init; }

    /// <summary>The device's address (or the camera's serial when OADM does not manage it).</summary>
    public string Device { get; init; } = string.Empty;

    public string Profile { get; init; } = string.Empty;

    public EnrollmentKind Kind { get; init; }

    public bool Ok { get; init; }

    /// <summary>Why it was refused, or "Valid until 2027-10-22".</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>The certificate it issued (upper hex; <see cref="EstMethods.GetCertificate"/>); null for a refusal and older entries.</summary>
    public string? SerialNumber { get; init; }
}

/// <summary>The payload of <see cref="EstMethods.GetCertificate"/>.</summary>
public sealed record CertificateQuery(string SerialNumber);

/// <summary>A certificate EST issued.</summary>
public sealed record IssuedCertificate
{
    /// <summary>Upper-case hex.</summary>
    public string SerialNumber { get; init; } = string.Empty;

    public Guid DeviceId { get; init; }

    public string Device { get; init; } = string.Empty;

    public string Profile { get; init; } = string.Empty;

    public DateTime IssuedUtc { get; init; }

    public DateTime NotAfterUtc { get; init; }

    /// <summary>SHA-256 of the certificate (upper hex): the set-up task pins an HTTPS certificate by it.</summary>
    public string? Fingerprint { get; init; }

    /// <summary>When OADM saw that the camera had not renewed it in time (event "Certificate not renewed").</summary>
    public DateTime? NotRenewedUtc { get; init; }

    /// <summary>When enrollment was removed from the camera, or the camera from OADM: no renewal is expected.</summary>
    public DateTime? RenewalStoppedUtc { get; init; }

    /// <summary>The camera did not renew it in time (a renewal replaces the record); the page's "Not renewed".</summary>
    [JsonIgnore]
    public bool NotRenewed => this is { NotRenewedUtc: not null, RenewalStoppedUtc: null };
}

/// <summary>A device and one of its profiles (profile in lower case).</summary>
public sealed record DeviceProfile(Guid DeviceId, string Profile);

/// <summary>A listener as the page shows it: "This server" or a site, and its state in plain words.</summary>
public sealed record ListenerState(Guid? SiteId, string Name, bool Ok, string Text);

/// <summary>The page's state.</summary>
public sealed record EstState
{
    public EstConfig Config { get; init; } = new();

    /// <summary>"Running on port 9454", "Port in use", "OADM PRO is not active", ...</summary>
    public string StatusText { get; init; } = string.Empty;

    public bool StatusOk { get; init; }

    public IReadOnlyList<ListenerState> Listeners { get; init; } = [];

    /// <summary>The "Listen on" choices: All interfaces, then the server's interfaces.</summary>
    public IReadOnlyList<InterfaceOption> Interfaces { get; init; } = [];

    public int CertificateCount { get; init; }

    /// <summary>PKI's EST policies the server applies (Enrollment, Sign-in CA): "2 policies apply". Null for a user limited to a device group.</summary>
    public int? PoliciesApply { get; init; }

    /// <summary>Those policies' names and summaries: the tooltip of "2 policies applied". Null for a user limited to a device group.</summary>
    public IReadOnlyList<Oadm.Plugins.Pki.Policies.PolicyLine>? PolicyLines { get; init; }

    /// <summary>Why no camera can enroll ("There is no Sign-in CA policy..."); null when none. Null for a user limited to a device group.</summary>
    public string? PolicyProblem { get; init; }

    /// <summary>The CA that issues the certificates (the OADM CA's name); null without one.</summary>
    public string? IssuingCa { get; init; }

    /// <summary>The HTTPS and IEEE 802.1X certificate policies of PKI a profile can name, oldest first.</summary>
    public IReadOnlyList<IssuingPolicyChoice> IssuingPolicies { get; init; } = [];
}

/// <summary>Texts of the profiles' issuing policies (the page, the server's checks and refusals).</summary>
public static class EstTexts
{
    public const string ChoosePolicy = "Choose an issuing policy.";
    public const string PolicyGone = "This policy no longer exists. Choose another one.";

    /// <summary>A request of a profile whose issuing policy cannot be read (fail closed).</summary>
    public const string NoPolicy = "The issuing policy of this profile cannot be read.";

    /// <summary>The set-up task: a profile without a usable issuing policy (before the first write).</summary>
    public static string ProfileWithoutPolicy(string profile) => $"The EST profile {profile} has no issuing policy. Choose one on the EST server page. Nothing was changed.";

    /// <summary>The page: PKI has no Sign-in CA policy (nothing signs in, fail closed).</summary>
    public const string NoSignInPolicy = "Cameras cannot sign in: there is no Sign-in CA policy.";

    /// <summary>The page: PKI has no Enrollment policy (every device is refused).</summary>
    public const string NoEnrollmentPolicies = "No camera can enroll: there is no Enrollment policy.";

    /// <summary>"The enrollment policy PTZ does not allow the profile dot1x." (Enrollments; the camera gets the answer of an unknown profile).</summary>
    public static string ProfileNotAllowed(string policy, string profile) => $"The enrollment policy {policy} does not allow the profile {profile}.";

    /// <summary>"The enrollment policy Storage room allows no enrollment."</summary>
    public static string EnrollmentOff(string policy) => $"The enrollment policy {policy} allows no enrollment.";
}

/// <summary>An issuing policy a profile can name: "HTTPS" (OADM Root CA SERVER01, 365 days, ECC).</summary>
/// <param name="Kind">"httpsCertificate" or "dot1xCertificate" (<c>PolicyKinds</c>).</param>
public sealed record IssuingPolicyChoice(Guid Id, string Name, string Kind, string Summary);

public sealed record EnrollmentList(IReadOnlyList<Enrollment> Enrollments);

/// <summary>The names of PKI's CAs on the EST page.</summary>
public static class SignInNames
{
    public const string AxisDeviceId = "Axis device ID";

    /// <summary>The OADM CA's name when PKI names none (its certificate unreadable).</summary>
    public const string Oadm = "OADM CA";
}

/// <param name="Certificates">The certificates the cameras did not renew in time (the first 500).</param>
/// <param name="Total">All of them.</param>
public sealed record CertificateList(IReadOnlyList<IssuedCertificate> Certificates, int Total);

public sealed record SaveRequest(EstConfig Config);

/// <summary>Null error = saved; else the field and its error, shown below the field.</summary>
public sealed record SaveReply(EstState? State, string? Field = null, string? Error = null);


/// <summary>
/// The payload of "Set up certificate enrollment". <paramref name="ReplaceDot1x"/>: also give cameras with IEEE 802.1X on
/// the IEEE 802.1X profile (their network access certificate is replaced; the RADIUS server must trust the OADM CA).
/// </summary>
public sealed record SetupPayload(bool ReplaceDot1x = false)
{
    /// <summary>Set up (default) or remove <see cref="Profiles"/>.</summary>
    public SetupAction Action { get; init; }

    /// <summary>
    /// The user answered the dialog's clock question with Yes (a camera is behind the certificate's start,
    /// docs/spec/pki.md "Clock check"): the task goes on and notes it in the log.
    /// </summary>
    public bool ClockConfirmed { get; init; }

    /// <summary>With <see cref="SetupAction.Remove"/>: the profile ids to remove from every selected camera (any profile).</summary>
    public IReadOnlyList<string> Profiles { get; init; } = [];

    /// <summary>The payload of a run; an empty one is Set up without replacement; unreadable (or an unknown action): null.</summary>
    public static SetupPayload? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new SetupPayload();
        }

        try
        {
            return JsonSerializer.Deserialize<SetupPayload>(json, EstJson.Options) is { } payload && Enum.IsDefined(payload.Action)
                ? payload with { Profiles = payload.Profiles ?? [] }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>JSON of the page calls and the stored settings.</summary>
public static class EstJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string? json) =>
        (string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options))
        ?? throw new ArgumentException("The request is empty.", nameof(json));
}

/// <summary>What a run of "Certificate enrollment" does.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SetupAction>))]
public enum SetupAction
{
    Setup = 0,
    Remove = 1,
}

/// <summary>The answer of the dialog's query <c>listProfiles</c>: the camera's enrollment profiles.</summary>
/// <param name="Supported">The camera has certificate enrollment (cert and cert-est v1).</param>
public sealed record CameraProfileList(bool Supported, IReadOnlyList<CameraProfileInfo> Profiles);

/// <summary>One enrollment profile on a camera, as the dialog lists it.</summary>
/// <param name="Status">The camera's status: ENROLLED, CONNECTED (reached the server, no certificate) or UNCONNECTED.</param>
public sealed record CameraProfileInfo(string Id, IReadOnlyList<string> Services, string? Server, string? Status);
