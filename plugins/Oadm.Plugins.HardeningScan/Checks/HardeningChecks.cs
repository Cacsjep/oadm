using System.Globalization;

using Oadm.Plugins.HardeningScan.Device;
using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Users;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.HardeningScan.Checks;

/// <summary>
/// The rules of the checked rows of <see cref="HardeningCatalog"/>: pure functions of <see cref="DeviceFacts"/> (decision table in
/// the plugin README). A failed read makes only the checks that need it <see cref="CheckState.Error"/>.
/// </summary>
public static class HardeningChecks
{
    /// <summary>The TLS 1.2 ciphers the hardening guide recommends (AXIS OS 12.11 default list).</summary>
    public static readonly IReadOnlyList<string> RecommendedCiphers =
    [
        "ECDHE-ECDSA-AES128-GCM-SHA256",
        "ECDHE-RSA-AES128-GCM-SHA256",
        "ECDHE-ECDSA-AES256-GCM-SHA384",
        "ECDHE-RSA-AES256-GCM-SHA384",
        "ECDHE-ECDSA-CHACHA20-POLY1305",
        "ECDHE-RSA-CHACHA20-POLY1305",
    ];

    /// <summary>Lowest AXIS OS with the UART debug port disabled by default.</summary>
    public static readonly Version UartSafeVersion = new(10, 11);

    /// <summary>
    /// One result per entry of <see cref="HardeningCatalog.Columns"/>: the columns of <paramref name="level"/> evaluated, the
    /// others <see cref="CheckState.NotScanned"/>.
    /// </summary>
    public static IReadOnlyList<CheckResult> Evaluate(DeviceFacts facts, ScanLevel level)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var results = new List<CheckResult>(HardeningCatalog.Columns.Count);
        foreach (var column in HardeningCatalog.Columns)
        {
            results.Add(column.Level == ScanLevel.Extended && level == ScanLevel.Basic
                ? new CheckResult { Id = column.Id, State = CheckState.NotScanned }
                : Evaluate(column.Id, facts));
        }

        return results;
    }

    /// <summary>
    /// Results of a device that was not contacted (refused status or not reachable): the checks that need no request (B2, B15,
    /// E3) from the cached device data, every other check Error with <paramref name="reason"/>.
    /// </summary>
    public static IReadOnlyList<CheckResult> CacheOnly(IDeviceInfo device, ScanLevel level, string reason, DateTimeOffset now)
    {
        var facts = new DeviceFacts { Device = device, Now = now };
        var results = new List<CheckResult>(HardeningCatalog.Columns.Count);
        foreach (var column in HardeningCatalog.Columns)
        {
            CheckResult result;
            if (column.Level == ScanLevel.Extended && level == ScanLevel.Basic)
            {
                result = new CheckResult { Id = column.Id, State = CheckState.NotScanned };
            }
            else if (column.Id is HardeningCatalog.AxisOs or HardeningCatalog.Uart or HardeningCatalog.Certificate)
            {
                result = Evaluate(column.Id, facts);
            }
            else
            {
                result = Error(column.Id, reason);
            }

            results.Add(result);
        }

        return results;
    }

    public static CheckResult Evaluate(string id, DeviceFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return id switch
        {
            HardeningCatalog.AxisOs => AxisOs(facts),
            HardeningCatalog.Accounts => Accounts(facts),
            HardeningCatalog.PasswordPolicy => PasswordPolicy(facts),
            HardeningCatalog.IpConfiguration => IpConfiguration(facts),
            HardeningCatalog.TimeSync => TimeSync(facts),
            HardeningCatalog.StorageEncryption => StorageEncryption(facts),
            HardeningCatalog.Applications => Applications(facts),
            HardeningCatalog.WebInterface => WebInterface(facts),
            HardeningCatalog.Discovery => Discovery(facts),
            HardeningCatalog.Audio => Audio(facts),
            HardeningCatalog.SdCardSlot => SdCardSlot(facts),
            HardeningCatalog.Ssh => Ssh(facts),
            HardeningCatalog.Uart => Uart(facts),
            HardeningCatalog.Firewall => Firewall(facts),
            HardeningCatalog.Ciphers => Ciphers(facts),
            HardeningCatalog.Certificate => Certificate(facts),
            HardeningCatalog.RemoteSyslog => RemoteSyslog(facts),
            HardeningCatalog.Snmp => Snmp(facts),
            HardeningCatalog.Rtsps => Rtsps(facts),
            HardeningCatalog.OAuth => OAuth(facts),
            HardeningCatalog.HttpsOnly => HttpsOnly(facts),
            HardeningCatalog.Dot1x => Dot1x(facts),
            HardeningCatalog.BruteForce => BruteForce(facts),
            HardeningCatalog.AccessLog => AccessLog(facts),
            HardeningCatalog.SignedVideo => SignedVideo(facts),
            HardeningCatalog.Nts => Nts(facts),
            _ => throw new ArgumentException($"Unknown check '{id}'.", nameof(id)),
        };
    }

    private static CheckResult AxisOs(DeviceFacts facts) =>
        Result(HardeningCatalog.AxisOs, CheckState.Info, string.IsNullOrEmpty(facts.Device.FirmwareVersion) ? "AXIS OS version not known yet" : "AXIS OS " + facts.Device.FirmwareVersion);

    private static CheckResult Accounts(DeviceFacts facts)
    {
        const string id = HardeningCatalog.Accounts;
        if (Problem(id, facts.Users) is { } problem)
        {
            return problem;
        }

        var users = facts.Users.Value;
        var admins = users.Count(u => u.Role == UserRole.Administrator);
        var operators = users.Count(u => u.Role == UserRole.Operator);
        var viewers = users.Count(u => u.Role is UserRole.Viewer or UserRole.None);
        var parts = new List<string> { Count(admins, "administrator", "administrators") };
        if (operators > 0)
        {
            parts.Add(Count(operators, "operator", "operators"));
        }

        if (viewers > 0)
        {
            parts.Add(Count(viewers, "viewer", "viewers"));
        }

        var value = string.Join(", ", parts);
        var detail = string.Join(", ", users.Select(u => $"{u.Name} ({RoleText(u.Role)})"));
        return Result(id, operators + viewers > 0 ? CheckState.Pass : CheckState.Warn, value, detail);
    }

    private static CheckResult PasswordPolicy(DeviceFacts facts)
    {
        const string id = HardeningCatalog.PasswordPolicy;
        if (Problem(id, facts.PasswordPolicy) is { } problem)
        {
            return problem;
        }

        var policy = facts.PasswordPolicy.Value;
        return policy.ToUpperInvariant() switch
        {
            "LENGTH" => Result(id, CheckState.Pass, "Password policy: long passwords (15 characters)"),
            "COMPLEX" => Result(id, CheckState.Pass, "Password policy: complex passwords"),
            "NONE" or "" => Result(id, CheckState.Warn, "Password policy: none"),
            _ => Result(id, CheckState.Warn, "Password policy: " + policy),
        };
    }

    private static CheckResult IpConfiguration(DeviceFacts facts)
    {
        const string id = HardeningCatalog.IpConfiguration;
        var dhcp = facts.Device.DhcpEnabled;
        if (dhcp is null && facts.Params.IsOk && facts.Params.Value["Network.BootProto"] is { } bootProto)
        {
            dhcp = string.Equals(bootProto.Trim(), "dhcp", StringComparison.OrdinalIgnoreCase);
        }

        return dhcp switch
        {
            true => Result(id, CheckState.Warn, "DHCP", "The guide recommends static IP addresses."),
            false => Result(id, CheckState.Pass, "Static IP address"),
            null => Result(id, CheckState.NotApplicable, "Not known yet (the next full refresh reads it)"),
        };
    }

    private static CheckResult TimeSync(DeviceFacts facts)
    {
        const string id = HardeningCatalog.TimeSync;
        if (Problem(id, facts.Time) is { } problem)
        {
            return problem;
        }

        var time = facts.Time.Value;
        if (time.NtpEnabled != true)
        {
            return Result(id, CheckState.Fail, "NTP off");
        }

        var dhcp = time.NtpSource == DateAndTime.Model.NtpSource.Dhcp;
        var servers = (dhcp ? time.AdvertisedServers : time.NtpServers).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        var parts = new List<string> { "NTP on", Count(servers.Count, "server", "servers") + (dhcp ? " from DHCP" : string.Empty) };
        if (time.Synced is { } synced)
        {
            parts.Add(synced ? "synchronized" : "not synchronized");
        }

        var state = servers.Count >= 2 && time.Synced != false ? CheckState.Pass : CheckState.Warn;
        return Result(id, state, string.Join(", ", parts), servers.Count > 0 ? "Servers: " + string.Join(", ", servers) : null);
    }

    private static CheckResult StorageEncryption(DeviceFacts facts)
    {
        const string id = HardeningCatalog.StorageEncryption;
        if (facts.Params.IsOk && facts.Params.Value.Bool("Properties.LocalStorage.DiskEncryption") == false)
        {
            return Result(id, CheckState.NotApplicable, "The device cannot encrypt its storage");
        }

        if (Problem(id, facts.Disks) is { } problem)
        {
            return problem;
        }

        var connected = facts.Disks.Value.Where(d => d.IsConnected).ToList();
        if (connected.Count == 0)
        {
            return Result(id, CheckState.NotApplicable, "No storage connected");
        }

        var plain = connected.Where(d => d.Encrypted != true).ToList();
        var detail = string.Join(", ", connected.Select(d => $"{d.Label}: {(d.Encrypted == true ? "encrypted" : "not encrypted")}"));
        return plain.Count == 0
            ? Result(id, CheckState.Pass, connected.Count == 1 ? connected[0].Label + " encrypted" : "All storage encrypted", detail)
            : Result(id, CheckState.Fail, string.Join(", ", plain.Select(d => d.Label)) + " not encrypted", detail);
    }

    private static CheckResult Applications(DeviceFacts facts)
    {
        const string id = HardeningCatalog.Applications;
        if (Problem(id, facts.Applications) is { } problem)
        {
            return problem;
        }

        var apps = facts.Applications.Value;
        var unsigned = apps.Count(a => !string.Equals(a.SignatureStatus, "Signed", StringComparison.OrdinalIgnoreCase));
        var stopped = apps.Count(a => !string.Equals(a.Status, "Running", StringComparison.OrdinalIgnoreCase));
        var findings = new List<string>();
        if (facts.AllowUnsigned == true)
        {
            findings.Add("unsigned applications allowed");
        }

        if (unsigned > 0)
        {
            findings.Add(string.Create(CultureInfo.InvariantCulture, $"{unsigned} of {apps.Count} not signed"));
        }

        if (stopped > 0)
        {
            findings.Add(string.Create(CultureInfo.InvariantCulture, $"{stopped} installed but stopped"));
        }

        var detail = apps.Count == 0
            ? null
            : string.Join(", ", apps.Select(a => $"{a.DisplayName} ({a.Status?.ToLowerInvariant() ?? "unknown"}, {(string.Equals(a.SignatureStatus, "Signed", StringComparison.OrdinalIgnoreCase) ? "signed" : "not signed")})"));
        if (findings.Count == 0)
        {
            return Result(id, CheckState.Pass, apps.Count == 0 ? "No applications installed" : Count(apps.Count, "signed application", "signed applications") + " running", detail);
        }

        var value = Capitalize(string.Join(", ", findings));
        return Result(id, CheckState.Warn, value, detail);
    }

    private static CheckResult WebInterface(DeviceFacts facts) =>
        Toggle(HardeningCatalog.WebInterface, facts, "System.WebInterfaceDisabled", onIsGood: true,
            on: "Web interface off", off: "Web interface on", offState: CheckState.Warn, missing: "No setting (needs AXIS OS 9.50 or later)");

    private static CheckResult Discovery(DeviceFacts facts)
    {
        const string id = HardeningCatalog.Discovery;
        if (Problem(id, facts.Params) is { } problem)
        {
            return problem;
        }

        var p = facts.Params.Value;
        var on = new List<string>();
        var known = 0;
        void Add(string name, bool? enabled)
        {
            if (enabled is not null)
            {
                known++;
            }

            if (enabled == true)
            {
                on.Add(name);
            }
        }

        Add("Bonjour", p.Bool("Network.Bonjour.Enabled"));
        Add("UPnP", p.Bool("Network.UPnP.Enabled"));
        Add("ZeroConf", p.Bool("Network.ZeroConf.Enabled"));
        Add("WS-Discovery", p.Bool("WebService.DiscoveryMode.Discoverable"));
        if (known == 0)
        {
            return Result(id, CheckState.NotApplicable, "No discovery settings on this device");
        }

        var lldp = facts.LldpActivated == true ? "LLDP is on (turning it off may affect PoE negotiation)." : null;
        return on.Count == 0
            ? Result(id, CheckState.Pass, "Discovery protocols off", lldp)
            : Result(id, CheckState.Warn, string.Join(", ", on) + " on", lldp);
    }

    private static CheckResult Audio(DeviceFacts facts)
    {
        const string id = HardeningCatalog.Audio;
        if (facts.Device.Category is DeviceCategory.Speaker or DeviceCategory.Intercom or DeviceCategory.Audio)
        {
            return Result(id, CheckState.NotApplicable, "Audio is the purpose of this device");
        }

        if (Problem(id, facts.Params) is { } problem)
        {
            return problem;
        }

        var p = facts.Params.Value;
        if (p.Bool("Properties.Audio.Audio") == false)
        {
            return Result(id, CheckState.Pass, "No audio");
        }

        var channels = p.WithPrefix("Audio.A").Where(x => x.Key.EndsWith(".Enabled", StringComparison.OrdinalIgnoreCase)).ToList();
        if (channels.Count == 0)
        {
            return p.Has("Properties.Audio.Audio") ? Result(id, CheckState.Pass, "Audio off") : Result(id, CheckState.Pass, "No audio");
        }

        var enabled = channels.Count(x => ParamList.ParseBool(x.Value) == true);
        return enabled == 0
            ? Result(id, CheckState.Pass, "Audio off")
            : Result(id, CheckState.Warn, string.Create(CultureInfo.InvariantCulture, $"Audio on ({enabled} of {channels.Count} channels)"));
    }

    private static CheckResult SdCardSlot(DeviceFacts facts)
    {
        const string id = HardeningCatalog.SdCardSlot;
        if (Problem(id, facts.Params) is { } problem)
        {
            return problem;
        }

        var p = facts.Params.Value;
        if (p.Bool("Properties.LocalStorage.SDCard") != true)
        {
            return Result(id, CheckState.NotApplicable, "No SD card slot");
        }

        var disks = facts.Disks.IsOk ? facts.Disks.Value : [];
        var sdGroups = disks.Where(d => d.IsSdCard && d.Group is not null).Select(d => d.Group!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (sdGroups.Count == 0)
        {
            sdGroups.Add("S0"); // the first storage group is the SD card slot
        }

        var findings = new List<string>();
        var warn = false;
        foreach (var group in sdGroups.Order(StringComparer.OrdinalIgnoreCase))
        {
            var enabled = p.Bool($"Storage.{group}.Enabled");
            var card = disks.FirstOrDefault(d => d.IsSdCard && string.Equals(d.Group, group, StringComparison.OrdinalIgnoreCase));
            if (enabled == false)
            {
                findings.Add("Slot off");
            }
            else if (card is { IsConnected: true })
            {
                findings.Add("Card in use");
            }
            else if (!facts.Disks.IsOk)
            {
                findings.Add("Slot on (card not known: " + (facts.Disks.Error ?? facts.Disks.Missing) + ")");
            }
            else
            {
                findings.Add("Slot on without a card");
                warn = true;
            }
        }

        return Result(id, warn ? CheckState.Warn : CheckState.Pass, string.Join(", ", findings.Distinct(StringComparer.Ordinal)));
    }

    private static CheckResult Ssh(DeviceFacts facts) =>
        Toggle(HardeningCatalog.Ssh, facts, "Network.SSH.Enabled", onIsGood: false,
            on: "SSH on", off: "SSH off", offState: CheckState.Fail, missing: "No SSH on this device");

    private static CheckResult Uart(DeviceFacts facts)
    {
        const string id = HardeningCatalog.Uart;
        if (ParseVersion(facts.Device.FirmwareVersion) is not { } version)
        {
            return Result(id, CheckState.NotApplicable, "AXIS OS version not known yet");
        }

        return version >= UartSafeVersion
            ? Result(id, CheckState.Pass, "Debug port off by default (AXIS OS " + facts.Device.FirmwareVersion + ")")
            : Result(id, CheckState.Warn, "AXIS OS " + facts.Device.FirmwareVersion + " is older than 10.11");
    }

    private static CheckResult Firewall(DeviceFacts facts)
    {
        const string id = HardeningCatalog.Firewall;
        if (facts.Firewall.IsOk)
        {
            var firewall = facts.Firewall.Value;
            var rules = Count(firewall.RuleTypes.Count, "rule", "rules");
            if (!firewall.Activated)
            {
                return Result(id, CheckState.Fail, "Firewall off");
            }

            if (string.Equals(firewall.DefaultPolicy, "DROP", StringComparison.OrdinalIgnoreCase))
            {
                return Result(id, CheckState.Pass, "Firewall on, default drop, " + rules);
            }

            var only = firewall.RuleTypes.Count > 0 && firewall.RuleTypes.All(t => string.Equals(t, "LIMIT", StringComparison.OrdinalIgnoreCase))
                ? " (limit rules only)"
                : string.Empty;
            return Result(id, CheckState.Warn, $"Firewall on, default {firewall.DefaultPolicy?.ToLowerInvariant() ?? "accept"}, {rules}{only}");
        }

        if (facts.Firewall.Error is { } error)
        {
            return Error(id, error);
        }

        // Older AXIS OS: the IP address filter of param.cgi.
        if (Problem(id, facts.Params) is { } problem)
        {
            return problem;
        }

        return facts.Params.Value.Bool("Network.Filter.Enabled") switch
        {
            true => Result(id, CheckState.Warn, "IP address filter on (no firewall on this firmware)"),
            false => Result(id, CheckState.Fail, "No firewall, IP address filter off"),
            null => Result(id, CheckState.NotApplicable, "No firewall or IP address filter on this firmware"),
        };
    }

    private static CheckResult Ciphers(DeviceFacts facts)
    {
        const string id = HardeningCatalog.Ciphers;
        var https = facts.Device.HttpsEnabled;
        if (https is null && facts.Params.IsOk)
        {
            https = facts.Params.Value.Bool("HTTPS.Enabled");
        }

        if (https == false)
        {
            return Result(id, CheckState.Fail, "HTTPS off");
        }

        IReadOnlyList<string>? ciphers = null;
        if (facts.Params.IsOk && facts.Params.Value["HTTPS.Ciphers"] is { Length: > 0 } list)
        {
            ciphers = list.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        else if (facts.WebServerTls.IsOk && facts.WebServerTls.Value.Ciphers.Count > 0)
        {
            ciphers = facts.WebServerTls.Value.Ciphers;
        }

        if (ciphers is null)
        {
            return facts.Params.Error is { } error ? Error(id, error) : Result(id, CheckState.NotApplicable, "Cipher list not available");
        }

        var other = ciphers.Where(c => !RecommendedCiphers.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        var detail = "Ciphers: " + string.Join(", ", ciphers);
        return other.Count == 0
            ? Result(id, CheckState.Pass, "Recommended ciphers only", detail)
            : Result(id, CheckState.Warn, Count(other.Count, "other cipher", "other ciphers") + ": " + string.Join(", ", other), detail);
    }

    private static CheckResult Certificate(DeviceFacts facts)
    {
        const string id = HardeningCatalog.Certificate;
        var device = facts.Device;
        if (device.CertTrustName is not { } trust)
        {
            return device.HttpsEnabled == false
                ? Result(id, CheckState.Fail, "HTTP only")
                : Result(id, CheckState.NotApplicable, "Certificate not checked yet");
        }

        var days = device.CertNotAfterUtc is { } end ? (int)Math.Floor((end - facts.Now.UtcDateTime).TotalDays) : (int?)null;
        var validity = days switch
        {
            null => string.Empty,
            < 0 => ", expired",
            0 => ", expires today",
            1 => ", expires in 1 day",
            _ => string.Create(CultureInfo.InvariantCulture, $", expires in {days} days"),
        };
        if (string.Equals(trust, "Expired", StringComparison.OrdinalIgnoreCase) || days < 0)
        {
            return Result(id, CheckState.Fail, "Certificate expired");
        }

        if (string.Equals(trust, "Untrusted", StringComparison.OrdinalIgnoreCase))
        {
            return Result(id, CheckState.Fail, "Untrusted certificate" + validity);
        }

        if (string.Equals(trust, "SelfSigned", StringComparison.OrdinalIgnoreCase))
        {
            return Result(id, CheckState.Warn, "Self-signed certificate" + validity);
        }

        return days <= HardeningScanPluginInfo.CertificateWarningDays
            ? Result(id, CheckState.Warn, "Trusted certificate" + validity)
            : Result(id, CheckState.Pass, "Trusted certificate" + validity);
    }

    private static CheckResult RemoteSyslog(DeviceFacts facts)
    {
        const string id = HardeningCatalog.RemoteSyslog;
        if (Problem(id, facts.Params) is { } problem)
        {
            return problem;
        }

        var p = facts.Params.Value;
        if (p.Bool("API.RemoteSyslog1.Enabled") is not { } enabled)
        {
            return Result(id, CheckState.NotApplicable, "No remote syslog on this firmware");
        }

        if (!enabled)
        {
            return Result(id, CheckState.Fail, "Remote syslog off");
        }

        var servers = new List<(string Address, string Protocol)>();
        for (var n = 1; n <= 8; n++)
        {
            var address = p[$"API.RemoteSyslog1.Server{n}.Address"];
            if (!string.IsNullOrWhiteSpace(address))
            {
                servers.Add((address.Trim(), (p[$"API.RemoteSyslog1.Server{n}.Protocol"] ?? "UDP").Trim()));
            }
        }

        if (servers.Count == 0)
        {
            return Result(id, CheckState.Fail, "Remote syslog on without a server");
        }

        var detail = string.Join(", ", servers.Select(s => $"{s.Address} ({s.Protocol})"));
        return servers.Any(s => string.Equals(s.Protocol, "TLS", StringComparison.OrdinalIgnoreCase))
            ? Result(id, CheckState.Pass, "Remote syslog over TLS", detail)
            : Result(id, CheckState.Warn, "Remote syslog unencrypted (" + string.Join(", ", servers.Select(s => s.Protocol).Distinct(StringComparer.OrdinalIgnoreCase)) + ")", detail);
    }

    private static CheckResult Snmp(DeviceFacts facts)
    {
        const string id = HardeningCatalog.Snmp;
        SnmpInfo? snmp = null;
        if (facts.Snmp.IsOk)
        {
            snmp = facts.Snmp.Value;
        }
        else if (facts.Snmp.Error is { } error)
        {
            return Error(id, error);
        }
        else if (facts.Params.IsOk)
        {
            snmp = DeviceParsers.ParseSnmpParameters(facts.Params.Value);
        }
        else if (facts.Params.Error is { } paramError)
        {
            return Error(id, paramError);
        }

        if (snmp is null)
        {
            return Result(id, CheckState.NotApplicable, "No SNMP on this device");
        }

        if (!snmp.Enabled)
        {
            return Result(id, CheckState.Pass, "SNMP off");
        }

        var versions = new List<string>();
        if (snmp.V1)
        {
            versions.Add("v1");
        }

        if (snmp.V2)
        {
            versions.Add("v2c");
        }

        if (snmp.V3)
        {
            versions.Add("v3");
        }

        var value = versions.Count == 0 ? "SNMP on" : "SNMP " + string.Join(", ", versions) + " on";
        return snmp.V1 || snmp.V2 ? Result(id, CheckState.Fail, value) : Result(id, CheckState.Pass, value);
    }

    private static CheckResult Rtsps(DeviceFacts facts)
    {
        if (!facts.Device.HasVideo && facts.Device.Category != DeviceCategory.Unknown)
        {
            return Result(HardeningCatalog.Rtsps, CheckState.NotApplicable, "No video");
        }

        return Toggle(HardeningCatalog.Rtsps, facts, "Network.RTSPS.Enabled", onIsGood: true,
            on: "RTSPS on", off: "RTSPS off", offState: CheckState.Warn, missing: "No RTSPS on this firmware");
    }

    private static CheckResult OAuth(DeviceFacts facts)
    {
        const string id = HardeningCatalog.OAuth;
        if (facts.OidcProvider.Missing is not null)
        {
            return Result(id, CheckState.NotApplicable, "Needs AXIS OS 11.6 or later");
        }

        if (Problem(id, facts.OidcProvider) is { } problem)
        {
            return problem;
        }

        var url = facts.OidcProvider.Value;
        return url.Length > 0
            ? Result(id, CheckState.Pass, "OpenID provider configured", url)
            : Result(id, CheckState.Warn, "Not configured");
    }

    private static CheckResult HttpsOnly(DeviceFacts facts)
    {
        const string id = HardeningCatalog.HttpsOnly;
        if (Problem(id, facts.WebServerTls) is { } problem)
        {
            return problem;
        }

        var policy = facts.WebServerTls.Value.Policy;
        var text = ConnectionPolicy.Describe(policy);
        return policy switch
        {
            _ when string.Equals(policy, ConnectionPolicy.Https, StringComparison.OrdinalIgnoreCase) => Result(id, CheckState.Pass, Capitalize(text)),
            _ when string.Equals(policy, ConnectionPolicy.Http, StringComparison.OrdinalIgnoreCase) => Result(id, CheckState.Fail, Capitalize(text)),
            _ => Result(id, CheckState.Warn, Capitalize(text)),
        };
    }

    private static CheckResult Dot1x(DeviceFacts facts)
    {
        const string id = HardeningCatalog.Dot1x;
        var enabled = facts.Device.Dot1xEnabled;
        if (enabled is null && facts.Params.IsOk)
        {
            enabled = facts.Params.Value.Bool("Network.Interface.I0.dot1x.Enabled");
        }

        return enabled switch
        {
            true => Result(id, CheckState.Pass, "IEEE 802.1X on"),
            false => Result(id, CheckState.Warn, "IEEE 802.1X off"),
            null => facts.Params.Error is { } error ? Error(id, error) : Result(id, CheckState.NotApplicable, "Not known yet (the next full refresh reads it)"),
        };
    }

    private static CheckResult BruteForce(DeviceFacts facts) =>
        Toggle(HardeningCatalog.BruteForce, facts, "System.PreventDoSAttack.ActivatePasswordThrottling", onIsGood: true,
            on: "Password throttling on", off: "Password throttling off", offState: CheckState.Fail, missing: "No brute-force protection setting on this firmware");

    private static CheckResult AccessLog(DeviceFacts facts) =>
        Toggle(HardeningCatalog.AccessLog, facts, "System.AccessLog", onIsGood: true,
            on: "Access log on", off: "Access log off", offState: CheckState.Warn, missing: "No access log setting on this firmware");

    private static CheckResult SignedVideo(DeviceFacts facts)
    {
        const string id = HardeningCatalog.SignedVideo;
        if (!facts.Device.HasVideo && facts.Device.Category != DeviceCategory.Unknown)
        {
            return Result(id, CheckState.NotApplicable, "No video");
        }

        if (facts.Apis.Count > 0 && !facts.Apis.Supports("signed-video", "1.0"))
        {
            return Result(id, CheckState.NotApplicable, "No signed video on this device");
        }

        if (Problem(id, facts.Params) is { } problem)
        {
            return problem;
        }

        var sources = facts.Params.Value.WithPrefix("Image.I").Where(x => x.Key.EndsWith(".MPEG.SignedVideo.Enabled", StringComparison.OrdinalIgnoreCase)).ToList();
        if (sources.Count == 0)
        {
            return Result(id, CheckState.NotApplicable, "No signed video on this device");
        }

        var on = sources.Count(x => ParamList.ParseBool(x.Value) == true);
        return on == sources.Count
            ? Result(id, CheckState.Pass, "Signed video on")
            : Result(id, CheckState.Warn, on == 0 ? "Signed video off" : string.Create(CultureInfo.InvariantCulture, $"Signed video on for {on} of {sources.Count} sources"));
    }

    private static CheckResult Nts(DeviceFacts facts)
    {
        const string id = HardeningCatalog.Nts;
        if (Problem(id, facts.Time) is { } problem)
        {
            return problem;
        }

        return facts.Time.Value.NtsEnabled switch
        {
            true => Result(id, CheckState.Pass, "NTS on"),
            false => Result(id, CheckState.Warn, "NTS off"),
            null => Result(id, CheckState.NotApplicable, "No NTS on this firmware (needs the NTP API 1.5)"),
        };
    }

    /// <summary>A yes/no parameter: <paramref name="onIsGood"/> says whether "yes" passes.</summary>
    private static CheckResult Toggle(string id, DeviceFacts facts, string parameter, bool onIsGood, string on, string off, CheckState offState, string missing)
    {
        if (Problem(id, facts.Params) is { } problem)
        {
            return problem;
        }

        return facts.Params.Value.Bool(parameter) switch
        {
            null => Result(id, CheckState.NotApplicable, missing),
            true => onIsGood ? Result(id, CheckState.Pass, on) : Result(id, offState, on),
            false => onIsGood ? Result(id, offState, off) : Result(id, CheckState.Pass, off),
        };
    }

    /// <summary>The result for a read that did not succeed (Error or not available), null when the value can be used.</summary>
    private static CheckResult? Problem<T>(string id, Fact<T> fact) =>
        fact.Error is { } error ? Error(id, error)
        : fact.Missing is { } missing ? Result(id, CheckState.NotApplicable, missing)
        : null;

    private static CheckResult Error(string id, string message) => Result(id, CheckState.Error, message);

    private static CheckResult Result(string id, CheckState state, string value, string? detail = null) =>
        new() { Id = id, State = state, Value = Cut(value, HardeningScanPluginInfo.MaxValueLength), Detail = detail is null ? null : Cut(detail, HardeningScanPluginInfo.MaxDetailLength) };

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Count(int n, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} {(n == 1 ? one : many)}");

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static string RoleText(UserRole role) => role switch
    {
        UserRole.Administrator => "administrator",
        UserRole.Operator => "operator",
        UserRole.Viewer => "viewer",
        _ => "no access group",
    };

    /// <summary>"12.11.77" -> 12.11; "10.12.338" -> 10.12; null when not a version.</summary>
    public static Version? ParseVersion(string? firmware)
    {
        if (string.IsNullOrWhiteSpace(firmware))
        {
            return null;
        }

        var parts = firmware.Trim().Split('.', '-', ' ');
        return parts.Length >= 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            ? new Version(major, minor)
            : null;
    }
}
