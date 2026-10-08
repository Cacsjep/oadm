namespace Oadm.Plugins.HardeningScan;

/// <summary>
/// The rows of the AXIS OS Hardening Guide (https://help.axis.com/en-us/axis-os-hardening-guide) the scan knows, in guide
/// order: Basic hardening (B), Extended hardening (E) and the additional Extended columns from "Default protection" and
/// "Legacy hardening" (X, user decision 2026-10-08: Extended only). Checked rows are grid columns; info rows cannot be
/// checked remotely and are listed once on the page with the guide's text.
/// </summary>
public static class HardeningCatalog
{
    public const string AxisOs = "B2";
    public const string Accounts = "B3";
    public const string PasswordPolicy = "B4";
    public const string IpConfiguration = "B5";
    public const string TimeSync = "B6";
    public const string StorageEncryption = "B7";
    public const string Applications = "B8";
    public const string WebInterface = "B9";
    public const string Discovery = "B10";
    public const string Audio = "B12";
    public const string SdCardSlot = "B13";
    public const string Ssh = "B14";
    public const string Uart = "B15";
    public const string Firewall = "B20";
    public const string Ciphers = "B21";
    public const string Certificate = "E3";
    public const string RemoteSyslog = "E4";
    public const string Snmp = "E5";
    public const string Rtsps = "E6";
    public const string OAuth = "E7";
    public const string HttpsOnly = "X1";
    public const string Dot1x = "X2";
    public const string BruteForce = "X3";
    public const string AccessLog = "X4";
    public const string SignedVideo = "X5";
    public const string Nts = "X6";

    private const string Commissioning = "Commissioning";
    private const string Services = "Disable unused services/functions";
    private const string Interfaces = "Interfaces";

    /// <summary>Every row in guide order.</summary>
    public static IReadOnlyList<CheckInfo> All { get; } =
    [
        Info("B1", ScanLevel.Basic, $"{Commissioning} > Factory default settings", "Factory default settings",
            "Start commissioning from factory default settings, so no setting of an earlier installation remains. OADM cannot tell whether a device was reset before it was commissioned."),
        Check(AxisOs, ScanLevel.Basic, $"{Commissioning} > Upgrade to latest AXIS OS", "Latest AXIS OS", "Latest OS",
            "Shown only: OADM has no list of the current AXIS OS versions yet.",
            "Upgrade to the latest AXIS OS of the device's track; it contains the latest security fixes.", rated: false),
        Check(Accounts, ScanLevel.Basic, $"{Commissioning} > Create dedicated accounts", "Dedicated accounts", "Accounts",
            "Pass: at least one account without administrator rights. Warning: administrator accounts only.",
            "Give every person and system its own account with only the rights it needs; use administrator accounts only for administration."),
        Check(PasswordPolicy, ScanLevel.Basic, $"{Commissioning} > Create dedicated accounts (password strength)", "Password policy", "Passwords",
            "Pass: the device requires long or complex passwords. Warning: no password requirements.",
            "Set the device's password policy to long (15 characters) or complex passwords, as NIST and BSI recommend."),
        Check(IpConfiguration, ScanLevel.Basic, "Configure network, date, and time > IP configuration", "IP configuration", "Static IP",
            "Pass: static IP address. Warning: the address comes from DHCP.",
            "Give devices static IP addresses so that their address is known and cannot be redirected by a rogue DHCP server."),
        Check(TimeSync, ScanLevel.Basic, "Configure network, date, and time > NTP/NTS", "Time synchronization", "Time",
            "Pass: NTP on with at least two servers and synchronized. Warning: one server or not synchronized. Fail: NTP off.",
            "Synchronize the time with at least two trusted NTP servers (NTS when available): correct time matters for certificates and logs."),
        Check(StorageEncryption, ScanLevel.Basic, "Edge storage encryption", "Edge storage encryption", "Encryption",
            "Pass: every connected disk is encrypted. Fail: a connected disk is not encrypted. Does not apply without a connected disk or encryption support.",
            "Encrypt SD cards and network shares, so recordings cannot be read when the card or share is taken. Use SMB 3 or later for network shares."),
        Check(Applications, ScanLevel.Basic, "Applications (ACAPs)", "Applications", "Apps",
            "Pass: only signed applications are installed and running, unsigned applications are not allowed. Warning: unsigned applications allowed or installed, or installed applications that are stopped.",
            "Install only the applications you need, signed ones only, and remove unused applications."),
        Check(WebInterface, ScanLevel.Basic, $"{Services} > Web interface access", "Web interface access", "Web UI",
            "Pass: the web interface is turned off. Warning: the web interface is on.",
            "Turn off the web interface when the device is managed by a video management system or OADM only (AXIS OS 9.50 or later)."),
        Check(Discovery, ScanLevel.Basic, $"{Services} > Network discovery protocols", "Network discovery protocols", "Discovery",
            "Pass: Bonjour, UPnP, ZeroConf and WS-Discovery are off. Warning: one of them is on.",
            "Turn off discovery protocols that are not needed, so the device does not announce itself on the network.",
            note: "OADM's Discovery and the re-find of moved devices use Bonjour: turning Bonjour off is a trade-off."),
        Info("B11", ScanLevel.Basic, $"{Services} > Information disclosure", "Information disclosure",
            "The guide describes which device information the web server discloses; it does not recommend changing it. AXIS OS 12 has no setting for it."),
        Check(Audio, ScanLevel.Basic, $"{Services} > Audio", "Audio", "Audio",
            "Pass: no audio or audio off. Warning: audio on in a camera, encoder or radar. Does not apply to speakers, intercoms and audio devices.",
            "Turn off audio when it is not used."),
        Check(SdCardSlot, ScanLevel.Basic, $"{Services} > SD card slot(s)", "SD card slot", "SD slot",
            "Pass: the slot is off or holds a card that is used. Warning: the slot is on without a card. Does not apply without a slot.",
            "Turn off SD card slots that are not used."),
        Check(Ssh, ScanLevel.Basic, $"{Services} > SSH access", "SSH access", "SSH",
            "Pass: SSH is off. Fail: SSH is on.",
            "Turn off SSH; turn it on only for troubleshooting and off again afterwards."),
        Check(Uart, ScanLevel.Basic, $"{Interfaces} > UART/Debug", "UART / debug port", "UART",
            "Pass: AXIS OS 10.11 or later (the debug port is off and needs a device-unique certificate to turn on). Warning: older AXIS OS.",
            "Use AXIS OS 10.11 or later: the UART debug interface is then disabled by default."),
        Info("B16", ScanLevel.Basic, $"{Interfaces} > Network ports", "Network ports",
            "Turn off unused network ports of switch products (e.g. AXIS S3008). Cameras have no such ports; not checked in this version."),
        Info("B17", ScanLevel.Basic, $"{Interfaces} > USB", "USB",
            "Turn off the USB port when it is not used (AXIS D1110)."),
        Info("B18", ScanLevel.Basic, $"{Interfaces} > Wi-Fi capabilities", "Wi-Fi",
            "Turn off Wi-Fi when it is not used."),
        Info("B19", ScanLevel.Basic, $"{Interfaces} > Bluetooth", "Bluetooth",
            "Turn off Bluetooth when it is not used."),
        Check(Firewall, ScanLevel.Basic, $"{Interfaces} > Limit network access", "Limit network access", "Firewall",
            "Pass: the firewall is on and drops what is not allowed. Warning: the firewall accepts by default (only limit rules), or an IP address filter is on. Fail: no firewall or filter on.",
            "Turn on the device firewall with a default policy that drops, and allow only the hosts and ports the system needs."),
        Check(Ciphers, ScanLevel.Basic, "HTTPS > HTTPS ciphers", "HTTPS ciphers", "Ciphers",
            "Pass: HTTPS on with only the TLS 1.2 ciphers the guide recommends. Warning: other ciphers. Fail: HTTPS off. TLS 1.3 cannot be configured.",
            "Use HTTPS and only the recommended TLS 1.2 ciphers (ECDHE with AES-GCM or ChaCha20-Poly1305)."),

        Info("E1", ScanLevel.Extended, "Limit internet and network exposure", "Network exposure",
            "Segment the network, reach devices from outside only through a VPN and inspect the traffic. This is network design and cannot be checked on the device; the detail shows whether the Axis remote service (O3C) is on."),
        Info("E2", ScanLevel.Extended, "Network vulnerability scanning", "Vulnerability scanning",
            "Scan the network for vulnerabilities regularly. This is an organizational measure."),
        Check(Certificate, ScanLevel.Extended, "Trusted public key infrastructure (PKI)", "Trusted certificate", "Certificate",
            "Pass: the HTTPS certificate is trusted and valid for more than 30 days. Warning: self-signed or expires within 30 days. Fail: untrusted, expired or HTTP only.",
            "Use HTTPS certificates from a trusted CA (e.g. the OADM PKI) instead of self-signed ones and renew them in time."),
        Check(RemoteSyslog, ScanLevel.Extended, "Remote syslog", "Remote syslog", "Syslog",
            "Pass: remote syslog on over TLS. Warning: on, but unencrypted (UDP or TCP). Fail: off.",
            "Send the device log to a remote syslog server over TLS, so events are kept and cannot be changed on the device."),
        Check(Snmp, ScanLevel.Extended, "SNMP", "SNMP", "SNMP",
            "Pass: SNMP off, or SNMP v3 only. Fail: SNMP v1 or v2c on.",
            "Use SNMP v3 only, or turn SNMP off; v1 and v2c send the community in clear text."),
        Check(Rtsps, ScanLevel.Extended, "Secure video streaming (SRTP/RTSPS)", "Secure video streaming", "RTSPS",
            "Pass: RTSPS on. Warning: off. Does not apply to devices without video.",
            "Turn on RTSPS (SRTP) and use it when the video management system supports it."),
        Check(OAuth, ScanLevel.Extended, "OAuth 2.0", "OAuth 2.0 / OpenID Connect", "OAuth",
            "Pass: an OpenID provider is configured. Warning: not configured. Does not apply below AXIS OS 11.6.",
            "Use central identity management (OpenID Connect) for the accounts that log in to the device."),
        Info("E8", ScanLevel.Extended, "Physical anti-tampering accessories", "Anti-tampering accessories",
            "Mount devices with anti-tampering accessories (casings, cable protection) where they can be reached."),

        Check(HttpsOnly, ScanLevel.Extended, "Legacy hardening > HTTPS only", "HTTPS only", "HTTPS only",
            "Pass: the web server accepts HTTPS only. Warning: HTTP and HTTPS. Fail: HTTP only.",
            "Allow HTTPS only, so no password or setting travels unencrypted."),
        Check(Dot1x, ScanLevel.Extended, "Default protection > IEEE 802.1X", "IEEE 802.1X", "802.1X",
            "Pass: IEEE 802.1X on. Warning: off.",
            "Use IEEE 802.1X (EAP-TLS) so only authenticated devices get network access."),
        Check(BruteForce, ScanLevel.Extended, "Default protection > Prevent brute-force attacks", "Brute-force protection", "Brute force",
            "Pass: password throttling on. Fail: off.",
            "Keep the brute-force protection (password throttling) on."),
        Check(AccessLog, ScanLevel.Extended, "Legacy hardening > Access log", "Access log", "Access log",
            "Pass: the access log is on. Warning: off.",
            "Turn on the access log, so logins and requests can be traced."),
        Check(SignedVideo, ScanLevel.Extended, "Axis Edge Vault > Signed video", "Signed video", "Signed video",
            "Pass: signed video on for every video source. Warning: off. Does not apply without the signed video feature.",
            "Turn on signed video, so recordings can be proven unchanged."),
        Check(Nts, ScanLevel.Extended, "Configure network, date, and time > NTP/NTS", "Network Time Security (NTS)", "NTS",
            "Pass: NTS on. Warning: off. Does not apply below NTP API 1.5.",
            "Use NTS so the time cannot be tampered with on the way."),
    ];

    /// <summary>The checked rows (grid columns) in order; the positions of <see cref="DeviceResult.States"/>.</summary>
    public static IReadOnlyList<CheckInfo> Columns { get; } = [.. All.Where(c => !c.IsInfo)];

    public static IReadOnlyList<string> ColumnIds { get; } = [.. Columns.Select(c => c.Id)];

    /// <summary>Position of a column id in <see cref="Columns"/>.</summary>
    public static IReadOnlyDictionary<string, int> ColumnIndex { get; } =
        Columns.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);

    /// <summary>The columns of a level: Basic = the Basic ones, Extended = all.</summary>
    public static IReadOnlyList<CheckInfo> ColumnsOf(ScanLevel level) =>
        level == ScanLevel.Extended ? Columns : [.. Columns.Where(c => c.Level == ScanLevel.Basic)];

    /// <summary>The info items of a level (Extended includes Basic's).</summary>
    public static IReadOnlyList<CheckInfo> InfoOf(ScanLevel level) =>
        [.. All.Where(c => c.IsInfo && (level == ScanLevel.Extended || c.Level == ScanLevel.Basic))];

    private static CheckInfo Check(string id, ScanLevel level, string section, string title, string header, string rule, string recommendation, bool rated = true, string? note = null) =>
        new() { Id = id, Level = level, Section = section, Title = title, Header = header, Rule = rule, Recommendation = recommendation, IsRated = rated, Note = note };

    private static CheckInfo Info(string id, ScanLevel level, string section, string title, string text) =>
        new() { Id = id, Level = level, Section = section, Title = title, Header = title, Rule = string.Empty, Recommendation = text, IsInfo = true, IsRated = false };
}
