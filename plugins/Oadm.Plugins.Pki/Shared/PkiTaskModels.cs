namespace Oadm.Plugins.Pki;

/// <summary>Ids and menu names of the contributed Security tasks (spec part 2), shared by server, dialogs and tests.</summary>
public static class PkiTaskIds
{
    public const string HttpsEnable = "oadm.pki.https-enable";
    public const string HttpsDisable = "oadm.pki.https-disable";
    public const string Dot1xEnable = "oadm.pki.dot1x-enable";
    public const string Dot1xDisable = "oadm.pki.dot1x-disable";
    public const string Renew = "oadm.pki.renew";
    public const string View = "oadm.pki.view";
    public const string Delete = "oadm.pki.delete";
    public const string Install = "oadm.pki.install";
    public const string InstallCa = "oadm.pki.install-ca";

    public const string HttpsEnableName = "Enable HTTPS";
    public const string HttpsDisableName = "Disable HTTPS";
    public const string Dot1xEnableName = "Enable IEEE 802.1X";
    public const string Dot1xDisableName = "Disable IEEE 802.1X";
    public const string RenewName = "Renew certificates";
    public const string ViewName = "View certificates";
    public const string DeleteName = "Delete certificates";
    public const string InstallName = "Install certificates";
    public const string InstallCaName = "Install CA certificates";

    /// <summary>Text of the HTTPS Disable confirmation.</summary>
    public const string HttpsDisableWarning = "Video systems that use HTTPS lose the connection to these devices.";

    /// <summary>Text of the 802.1X Enable confirmation.</summary>
    public const string Dot1xWarning = "Devices on ports that enforce 802.1X become unreachable if authentication fails.";
}

/// <summary>Query methods (<c>ITaskPluginQuery</c>) of the certificate dialogs; read-only.</summary>
public static class PkiQueries
{
    /// <summary>() -> <see cref="CertificateListReply"/> for one device.</summary>
    public const string ListCertificates = "listCertificates";

    /// <summary>
    /// () -> <see cref="IssuingPolicyList"/>: the issuing policies of the task's kind (Enable HTTPS: HTTPS certificate;
    /// Enable IEEE 802.1X, Renew certificates, Install certificates: IEEE 802.1X certificate), oldest first. Asked once with
    /// the first selected device.
    /// </summary>
    public const string ListIssuingPolicies = "listIssuingPolicies";
}

/// <summary>The answer of <see cref="PkiQueries.ListIssuingPolicies"/>.</summary>
public sealed record IssuingPolicyList
{
    /// <summary>The policies of the kind, oldest first (the first is preselected).</summary>
    public IReadOnlyList<Policies.PkiPolicy> Policies { get; init; } = [];

    /// <summary>The OADM CA's name ("OADM Root CA SERVER01"); null without a CA.</summary>
    public string? CaName { get; init; }

    /// <summary>The name of the CA each policy issues with ("Issued by"), by policy id.</summary>
    public IReadOnlyDictionary<Guid, string> CaNames { get; init; } = new Dictionary<Guid, string>();

    /// <summary>The name of the RADIUS server CA of each IEEE 802.1X policy, by policy id.</summary>
    public IReadOnlyDictionary<Guid, string> RadiusCaNames { get; init; } = new Dictionary<Guid, string>();

    /// <summary>What each policy issues, its summary sentence (below the dialog's Policy field), by policy id.</summary>
    public IReadOnlyDictionary<Guid, string> Summaries { get; init; } = new Dictionary<Guid, string>();

    /// <summary>Why there are none (the policies cannot be read, the PKI does not run); null otherwise.</summary>
    public string? Error { get; init; }
}

/// <summary>
/// Payload of Enable HTTPS and Enable IEEE 802.1X: the issuing policy the dialog chose; the run uses its values as stored
/// (decision 2026-10-25: no values of their own in the dialog, the policy decides). No policy: the first of the kind.
/// Payloads are never stored.
/// </summary>
public sealed record IssuePayload
{
    /// <summary>The user answered the clock question with Yes (<c>CameraClock</c>).</summary>
    public bool ClockConfirmed { get; init; }

    public Guid? PolicyId { get; init; }
}

/// <summary>
/// Payload of "Renew certificates": the IEEE 802.1X certificate policy the dialog chose (HTTPS renews with the first HTTPS
/// certificate policy) and the clock answer. No policy id (older payloads): the first IEEE 802.1X policy. Never stored.
/// </summary>
public sealed record RenewPayload
{
    /// <summary>The user answered the clock question with Yes (<c>CameraClock</c>).</summary>
    public bool ClockConfirmed { get; init; }

    /// <summary>The IEEE 802.1X certificate policy of the renewal.</summary>
    public Guid? Dot1xPolicyId { get; init; }
}

/// <summary>Group of an installed certificate in the view and delete dialogs.</summary>
public static class CertificateKind
{
    public const string Client = "client";
    public const string Server = "server";
    public const string Ca = "ca";
}

/// <summary>One certificate installed on a device, as the dialogs show it.</summary>
public sealed record InstalledCertificate
{
    public string Alias { get; init; } = string.Empty;

    /// <summary><see cref="CertificateKind"/>.</summary>
    public string Kind { get; init; } = CertificateKind.Server;

    /// <summary>Common name of the issuer.</summary>
    public string IssuedBy { get; init; } = string.Empty;

    /// <summary>Common name of the subject.</summary>
    public string IssuedTo { get; init; } = string.Empty;

    public DateTime NotAfterUtc { get; init; }

    /// <summary>"HTTPS", "802.1X" (the services using it), empty when unused.</summary>
    public IReadOnlyList<string> InUse { get; init; } = [];

    /// <summary>OADM issued it (registry or alias).</summary>
    public bool FromOadm { get; init; }

    /// <summary>An Axis factory device ID certificate (802.1AR): never deletable.</summary>
    public bool Factory { get; init; }

    /// <summary>SHA-256 (upper hex).</summary>
    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>The certificate's serial number (upper hex); empty when it could not be read.</summary>
    public string SerialNumber { get; init; } = string.Empty;
}

/// <summary>Answer of <see cref="PkiQueries.ListCertificates"/>.</summary>
public sealed record CertificateListReply
{
    public IReadOnlyList<InstalledCertificate> Certificates { get; init; } = [];

    /// <summary>The device cannot be read or is not compatible (user text).</summary>
    public string? Error { get; init; }
}

/// <summary>One certificate to delete.</summary>
public sealed record CertificateRef(string Alias, bool Ca);

/// <summary>Payload of "Delete certificates": the aliases per device id (the server re-checks "in use").</summary>
public sealed record DeletePayload
{
    public IReadOnlyDictionary<Guid, IReadOnlyList<CertificateRef>> Devices { get; init; } = new Dictionary<Guid, IReadOnlyList<CertificateRef>>();
}

/// <summary>What "Install certificates" sets up with the installed certificate.</summary>
public static class InstallPurpose
{
    public const string Https = "https";
    public const string Dot1x = "dot1x";

    /// <summary>The certificates of the file go to the device as CA certificates only.</summary>
    public const string CaOnly = "ca";
}

/// <summary>One file of "Install certificates", matched to one device.</summary>
public sealed record InstallFile(Guid DeviceId, string FileId, string FileName);

/// <summary>Payload of "Install certificates". The password stays in memory (payloads are never persisted).</summary>
public sealed record InstallPayload
{
    /// <summary><see cref="InstallPurpose"/>.</summary>
    public string Purpose { get; init; } = InstallPurpose.Https;

    /// <summary>One password for all files (like ADM).</summary>
    public string? Password { get; init; }

    public IReadOnlyList<InstallFile> Files { get; init; } = [];

    /// <summary>
    /// IEEE 802.1X: the IEEE 802.1X certificate policy whose settings and RADIUS server CA the run sets (the file brings the
    /// certificate). Null (older payloads): the first of the kind.
    /// </summary>
    public Guid? PolicyId { get; init; }
}

/// <summary>One CA certificate of "Install CA certificates": the name for the step and the PEM.</summary>
public sealed record CaCertificatePayload(string Name, string Pem);

/// <summary>
/// Payload of "Install CA certificates": the CA certificates for every selected device (public data only, a few KB each,
/// so no upload). The task checks every one again before the first write.
/// </summary>
public sealed record InstallCaPayload
{
    public IReadOnlyList<CaCertificatePayload> Certificates { get; init; } = [];
}
