using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Sdk.Certificates;

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

    /// <summary>Certificates shown on one page of the Certificates list.</summary>
    public const int CertificatesPageSize = 500;

    /// <summary>Event "Certificate enrolled" (device, profile, new or renewal).</summary>
    public const string EnrolledEvent = "oadm.est.enrolled";

    /// <summary>Event "Enrollment refused" (device, profile, reason).</summary>
    public const string RefusedEvent = "oadm.est.refused";
}

/// <summary>Page backend methods (<c>InvokeAsync</c>) and live event topics.</summary>
public static class EstMethods
{
    /// <summary>() -> <see cref="EstState"/>.</summary>
    public const string GetState = "getState";

    /// <summary>() -> <see cref="EnrollmentList"/>.</summary>
    public const string GetEnrollments = "getEnrollments";

    /// <summary><see cref="CertificatesRequest"/> -> <see cref="CertificateList"/>.</summary>
    public const string GetCertificates = "getCertificates";

    /// <summary><see cref="SaveRequest"/> -> <see cref="SaveReply"/> (Admin).</summary>
    public const string Save = "save";

    /// <summary><see cref="RevokeRequest"/> -> <see cref="RevokeReply"/> (Admin).</summary>
    public const string Revoke = "revoke";

    /// <summary>Event: <see cref="EstState"/> changed (status, a relay, the settings).</summary>
    public const string StateTopic = "state";

    /// <summary>Event: new enrollments (<see cref="EnrollmentList"/> of the new ones).</summary>
    public const string EnrollmentsTopic = "enrollments";
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

/// <summary>An EST profile: one path, one kind of certificate.</summary>
/// <param name="Name">The path segment and name ("https"): letters, digits and '_', 1..32 characters (the camera refuses '-' in its profile ids).</param>
/// <param name="Use">What the camera uses the certificate for.</param>
/// <param name="ValidityDays">How long each certificate is valid (1..825).</param>
/// <param name="Key">The key the camera creates.</param>
/// <param name="Enabled">Off: the path answers like an unknown one.</param>
public sealed record EstProfile(string Name, ProfileUse Use, int ValidityDays = 365, ProfileKey Key = ProfileKey.Ecc, bool Enabled = true)
{
    /// <summary>Server certificates for HTTPS and RTSPS, client certificates for IEEE 802.1X and MQTT.</summary>
    [JsonIgnore]
    public DeviceCertificateUse CertificateUse => Use is ProfileUse.Dot1x or ProfileUse.Mqtt ? DeviceCertificateUse.Client : DeviceCertificateUse.Server;
}

/// <summary>Plugin setting <c>config</c>.</summary>
public sealed record EstConfig
{
    public int Port { get; init; } = EstPluginInfo.DefaultPort;

    public IReadOnlyList<EstProfile> Profiles { get; init; } = Defaults;

    /// <summary>Enrollments kept (newest first), 100..5000.</summary>
    public int EnrollmentsKept { get; init; } = EstPluginInfo.DefaultEnrollmentsKept;

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
}

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

    public DateTime? RevokedUtc { get; init; }

    [JsonIgnore]
    public bool Revoked => RevokedUtc is not null;
}

/// <summary>A device and profile whose certificate was revoked: no more certificates until the camera is set up again.</summary>
public sealed record RevokedPair(Guid DeviceId, string Profile);

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

    public int CertificateCount { get; init; }
}

public sealed record EnrollmentList(IReadOnlyList<Enrollment> Enrollments);

/// <param name="Search">Device or profile contains it; empty = every certificate.</param>
/// <param name="Offset">The first entry of the page.</param>
public sealed record CertificatesRequest(string? Search = null, int Offset = 0);

/// <param name="Total">The matching certificates.</param>
public sealed record CertificateList(IReadOnlyList<IssuedCertificate> Certificates, int Total);

public sealed record SaveRequest(EstConfig Config);

/// <summary>Null error = saved; else the field and its error, shown below the field.</summary>
public sealed record SaveReply(EstState? State, string? Field = null, string? Error = null);

public sealed record RevokeRequest(IReadOnlyList<string> SerialNumbers);

public sealed record RevokeReply(int Revoked);

/// <summary>JSON of the page calls and the stored settings.</summary>
public static class EstJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string? json) =>
        (string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options))
        ?? throw new ArgumentException("The request is empty.", nameof(json));
}
