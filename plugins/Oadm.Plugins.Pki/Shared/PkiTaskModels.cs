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

    public const string HttpsEnableName = "HTTPS: Enable/Update";
    public const string HttpsDisableName = "HTTPS: Disable";
    public const string Dot1xEnableName = "IEEE 802.1X: Enable/Update";
    public const string Dot1xDisableName = "IEEE 802.1X: Disable";
    public const string RenewName = "Renew certificates now";
    public const string ViewName = "View installed certificates";
    public const string DeleteName = "Delete certificates";
    public const string InstallName = "Install certificates manually";

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

/// <summary>What "Install certificates manually" sets up with the installed certificate.</summary>
public static class InstallPurpose
{
    public const string Https = "https";
    public const string Dot1x = "dot1x";

    /// <summary>The certificates of the file go to the device as CA certificates only.</summary>
    public const string CaOnly = "ca";
}

/// <summary>One file of "Install certificates manually", matched to one device.</summary>
public sealed record InstallFile(Guid DeviceId, string FileId, string FileName);

/// <summary>Payload of "Install certificates manually". The password stays in memory (payloads are never persisted).</summary>
public sealed record InstallPayload
{
    /// <summary><see cref="InstallPurpose"/>.</summary>
    public string Purpose { get; init; } = InstallPurpose.Https;

    /// <summary>One password for all files (like ADM).</summary>
    public string? Password { get; init; }

    public IReadOnlyList<InstallFile> Files { get; init; } = [];
}
