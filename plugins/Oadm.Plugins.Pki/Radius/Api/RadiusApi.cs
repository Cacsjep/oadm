using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

using Oadm.Sdk.Certificates;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.Radius.Api;

// The page contract of the 802.1X server (docs/spec/radius-server.md): method names, event topics, the DTOs and the input
// rules. Shared by the server part and the page (Oadm.Plugins.Pki.Client references the server assembly): only the BCL and the
// SDK, never the protocol layer or BouncyCastle. JSON: camelCase (RadiusJson), enums as camelCase strings.

/// <summary>The page methods (<c>InvokeAsync</c>).</summary>
public static class RadiusMethods
{
    /// <summary>() -> <see cref="RadiusState"/>.</summary>
    public const string GetState = "getState";

    /// <summary><see cref="RadiusSettings"/> -> <see cref="RadiusState"/>. Users with all devices.</summary>
    public const string SaveSettings = "saveSettings";

    /// <summary>() -> <see cref="SwitchList"/>.</summary>
    public const string ListSwitches = "listSwitches";

    /// <summary><see cref="SaveSwitchRequest"/> -> <see cref="SwitchRow"/>. Users with all devices.</summary>
    public const string SaveSwitch = "saveSwitch";

    /// <summary>() -> <see cref="GeneratedSecret"/>: a random shared secret, nothing stored. Users with all devices.</summary>
    public const string GenerateSecret = "generateSecret";

    /// <summary><see cref="SwitchRef"/> -> (). Users with all devices.</summary>
    public const string DeleteSwitch = "deleteSwitch";

    /// <summary>() -> <see cref="SessionList"/>: the open sessions (and those ended in the last 10 s) of the caller's devices.</summary>
    public const string ListSessions = "listSessions";

    /// <summary><see cref="LogQuery"/> -> <see cref="LogPage"/>: the log of the caller's devices, newest first.</summary>
    public const string ListLog = "listLog";

    /// <summary>() -> (): deletes every log entry. Users with all devices.</summary>
    public const string DeleteLog = "deleteLog";

    /// <summary><see cref="LogEntryRef"/> -> <see cref="LogCertificate"/>: the client certificate of a log row.</summary>
    public const string GetLogCertificate = "getLogCertificate";

    /// <summary>() -> <see cref="SiteList"/>: the bound sites (the Site of a switch); a group user: the group's sites.</summary>
    public const string ListSites = "listSites";
}

/// <summary>The page's names of the methods beyond the first contract (the same values as <see cref="RadiusMethods"/>).</summary>
public static class RadiusPageMethods
{
    public const string ListSites = RadiusMethods.ListSites;
}

/// <summary>Limits of the page.</summary>
public static class RadiusPageLimits
{
    /// <summary>The most open sessions (the session table's limit).</summary>
    public const int MaxSessions = 10_000;

    /// <summary>Ended sessions leave the Sessions tab after this (also without the server's removal).</summary>
    public static readonly TimeSpan EndedSessionShown = TimeSpan.FromSeconds(10);
}

/// <summary>The states of <see cref="SessionRow.State"/> the page colors (camelCase names of the protocol's states).</summary>
public static class SessionStates
{
    public const string Authenticated = "authenticated";
    public const string Failed = "failed";
    public const string TimedOut = "timedOut";
    public const string Abandoned = "abandoned";
}

/// <summary>The answer of <see cref="RadiusMethods.ListSites"/>.</summary>
public sealed record SiteList(IReadOnlyList<SiteRef> Sites);

/// <summary>The live event topics (<c>WatchEventsAsync</c>).</summary>
public static class RadiusTopics
{
    /// <summary><see cref="RadiusState"/>: users with all devices and each device group get their own.</summary>
    public const string State = "state";

    /// <summary>
    /// A JSON array of <see cref="SessionRow"/> (changed rows, at most every 500 ms); a row with <see cref="SessionRow.Removed"/>
    /// leaves the tab. Each user gets the rows of the devices the user sees; rows without a device only users with all devices.
    /// </summary>
    public const string Sessions = "sessions";

    /// <summary>A JSON array of new <see cref="LogRow"/>, newest first (same audiences as <see cref="Sessions"/>).</summary>
    public const string Log = "log";

    /// <summary>{}: Delete all of the log; every page empties its log.</summary>
    public const string LogCleared = "logCleared";

    /// <summary><see cref="SwitchList"/>: the switches changed (added, edited, removed, a new state).</summary>
    public const string Switches = "switches";
}

/// <summary>Ids, defaults and limits shared by the server part and the page.</summary>
public static class RadiusLimits
{
    public const int DefaultPort = 1812;
    public const int DefaultLogKept = 5_000;
    public const int MinLogKept = 1_000;
    public const int MaxLogKept = 50_000;
    public const int MaxSwitches = 500;

    /// <summary>The shortest shared secret (8 characters, decision 2026-10-24: switches of some vendors take no longer ones; Generate makes 24).</summary>
    public const int MinSecretLength = 8;
    public const int MaxSecretLength = 128;
    public const int MaxNameLength = 64;
    public const int MaxLogPage = 500;
}

/// <summary>Switch states of <see cref="SwitchRow.State"/>.</summary>
public static class SwitchStates
{
    public const string Answered = "answered";
    public const string NoRequests = "noRequests";
    public const string WrongSecret = "wrongSecret";
    public const string SiteOffline = "siteOffline";
}

/// <summary>Results of <see cref="LogRow.Result"/>.</summary>
public static class LogResults
{
    public const string Authenticated = "authenticated";
    public const string Failed = "failed";
}

/// <summary>The settings of the Server card (the TLS settings are the 802.1X server policy of PKI).</summary>
public sealed record RadiusSettings
{
    /// <summary>The interface id of "Listen on" (<see cref="InterfaceOptions.AllId"/> = every address).</summary>
    public string ListenOn { get; init; } = InterfaceOptions.AllId;

    /// <summary>The name of that interface when it was saved (shown as "(not available)" while it is gone). Set by the server.</summary>
    public string? ListenOnName { get; init; } = InterfaceOptions.AllLabel;

    public int Port { get; init; } = RadiusLimits.DefaultPort;

    /// <summary>Log entries kept (1,000..50,000).</summary>
    public int LogKept { get; init; } = RadiusLimits.DefaultLogKept;
}

/// <summary>The server's own certificate (issued by the OADM CA).</summary>
public sealed record ServerCertificateInfo(DateTime NotAfterUtc, IReadOnlyList<string> Names);

/// <summary>A site whose node has not allowed UDP 1812 yet (the node app asks).</summary>
public sealed record SiteRef(Guid SiteId, string SiteName);

/// <summary>Requests from addresses that are no configured switch (dropped).</summary>
/// <param name="Count">Since the server started.</param>
/// <param name="LastAddress">The address of the last one (at a site: as the node saw it).</param>
/// <param name="LastSiteName">Its site, null on this server's network.</param>
public sealed record UnknownRequests(long Count, string? LastAddress, string? LastSiteName, DateTime? LastUtc);

/// <summary>The page's state (<see cref="RadiusMethods.GetState"/>).</summary>
public sealed record RadiusState
{
    /// <summary>The server answers switches.</summary>
    public bool Running { get; init; }

    /// <summary>"Running on port 1812", "OADM PRO is not active.", ... (left of Save).</summary>
    public string StatusText { get; init; } = string.Empty;

    /// <summary><see cref="ServiceStatus"/> kind: ok, neutral, warning, error.</summary>
    public string StatusKind { get; init; } = ServiceStatus.Neutral;

    public RadiusSettings Settings { get; init; } = new();

    /// <summary>The policies of PKI the server applies (802.1X server, Allow CA, VLAN, Block, Revoke): "6 policies apply".</summary>
    public int PoliciesApply { get; init; }

    /// <summary>Those policies' names and summaries: the tooltip of "6 policies applied".</summary>
    public IReadOnlyList<Oadm.Plugins.Pki.Policies.PolicyLine> PolicyLines { get; init; } = [];

    /// <summary>"Listen on": All interfaces first, then the server's interfaces.</summary>
    public IReadOnlyList<InterfaceOption> Interfaces { get; init; } = [];

    /// <summary>Null while the server has none (no CA).</summary>
    public ServerCertificateInfo? ServerCertificate { get; init; }

    public IReadOnlyList<SiteRef> SitesNotAllowed { get; init; } = [];

    /// <summary>The caller has all devices (the Server card and Switches can be changed).</summary>
    public bool HasAllDevices { get; init; }

    public UnknownRequests UnknownRequests { get; init; } = new(0, null, null, null);
}

/// <summary>One switch row.</summary>
public sealed record SwitchRow
{
    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>IP address or host name.</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>Null = this server's network.</summary>
    public Guid? SiteId { get; init; }

    /// <summary>"This server" or the site's name.</summary>
    public string SiteName { get; init; } = string.Empty;

    /// <summary>One of <see cref="SwitchStates"/>.</summary>
    public string State { get; init; } = SwitchStates.NoRequests;

    public DateTime? LastRequestUtc { get; init; }

    /// <summary>Requests dropped (malformed, wrong secret) since the server started.</summary>
    public long DroppedCount { get; init; }

    /// <summary>
    /// Requests without 802.1X (a MAC address or password check of the switch) since the server started: not answered and
    /// never shown as a wrong secret.
    /// </summary>
    public long MacOnlyCount { get; init; }

    /// <summary>The address the switch must use as RADIUS server (this server's, or the site node's); null when not known.</summary>
    public string? RadiusServerAddress { get; init; }
}

public sealed record SwitchList(IReadOnlyList<SwitchRow> Switches);

/// <summary>Add (no id) or edit a switch. An empty secret on edit keeps the current one.</summary>
public sealed record SaveSwitchRequest
{
    public Guid? Id { get; init; }

    public string? Name { get; init; }

    public string? Address { get; init; }

    public Guid? SiteId { get; init; }

    public string? Secret { get; init; }
}

public sealed record SwitchRef(Guid Id);

public sealed record GeneratedSecret(string Secret);

/// <summary>Fragment n of m of a session.</summary>
public sealed record FragmentInfo(int N, int M);

/// <summary>One row of the Sessions tab.</summary>
public sealed record SessionRow
{
    public Guid Id { get; init; }

    public DateTime StartedUtc { get; init; }

    public string SwitchName { get; init; } = string.Empty;

    public string Port { get; init; } = string.Empty;

    /// <summary>"Ethernet", "Wireless", ...; null when the switch sent none.</summary>
    public string? PortType { get; init; }

    /// <summary>"AC:CC:8E:12:34:56", as sent when it is not a MAC address, empty when none.</summary>
    public string Mac { get; init; } = string.Empty;

    /// <summary>The EAP identity (shown, never trusted).</summary>
    public string? Identity { get; init; }

    public Guid? DeviceId { get; init; }

    /// <summary>The device's host name, else its serial number; empty for an unknown device.</summary>
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>The device is a guess from the MAC address (italics) until the certificate named it.</summary>
    public bool DeviceGuess { get; init; }

    /// <summary>The device's last known address; empty for an unknown device.</summary>
    public string IpAddress { get; init; } = string.Empty;

    /// <summary>The state (camelCase name: waitingForIdentity, ..., authenticated, failed, timedOut, abandoned).</summary>
    public string State { get; init; } = string.Empty;

    /// <summary>The state in plain words ("Sending server certificate: fragment 2 of 4").</summary>
    public string StateText { get; init; } = string.Empty;

    public FragmentInfo? Fragment { get; init; }

    public long DurationMs { get; init; }

    public int? Vlan { get; init; }

    /// <summary>The VLAN policy that gave <see cref="Vlan"/> ("VLAN 30 by Building B"); null for none.</summary>
    public string? VlanPolicy { get; init; }

    /// <summary>"TLS 1.2", "TLS 1.3"; null before it is known.</summary>
    public string? TlsVersion { get; init; }

    /// <summary>The session ended and left the tab (events only).</summary>
    public bool Removed { get; init; }
}

public sealed record SessionList(IReadOnlyList<SessionRow> Sessions);

/// <summary>A page of the log.</summary>
/// <param name="Search">Text in any column (case-insensitive); null or empty = every row.</param>
/// <param name="Limit">1..500.</param>
public sealed record LogQuery(string? Search, int Offset = 0, int Limit = 500)
{
    /// <summary>
    /// The page's time zone (<c>TimeZoneInfo.Local.Id</c>): the search also finds the time as the page shows it
    /// ("2026-10-24 14:05"); null or unknown: the UTC time only.
    /// </summary>
    public string? TimeZoneId { get; init; }
}

/// <summary>One log entry.</summary>
public sealed record LogRow
{
    public long Id { get; init; }

    public DateTime TimeUtc { get; init; }

    public string SwitchName { get; init; } = string.Empty;

    public string Port { get; init; } = string.Empty;

    public string Mac { get; init; } = string.Empty;

    public string? Identity { get; init; }

    public Guid? DeviceId { get; init; }

    /// <summary>The device's host name, else its serial number; empty for an unknown device.</summary>
    public string DeviceName { get; init; } = string.Empty;

    /// <summary>The device is only a guess from the MAC address (the certificate named none).</summary>
    public bool DeviceGuess { get; init; }

    public string IpAddress { get; init; } = string.Empty;

    /// <summary>One of <see cref="LogResults"/>.</summary>
    public string Result { get; init; } = LogResults.Failed;

    /// <summary>Why it failed; null for an authenticated device.</summary>
    public string? Reason { get; init; }

    /// <summary>A warning of an authenticated device ("MAC address ... does not match the certificate of ...").</summary>
    public string? Warning { get; init; }

    public int? Vlan { get; init; }

    /// <summary>The VLAN policy that gave <see cref="Vlan"/> ("VLAN 30 by Building B"); null for none.</summary>
    public string? VlanPolicy { get; init; }

    /// <summary>The serial number of the client certificate, when the device sent one.</summary>
    public string? CertificateSerial { get; init; }

    /// <summary>View certificate has a certificate to show.</summary>
    public bool HasCertificate { get; init; }

    /// <summary>
    /// A row of dropped requests (a sender that is no switch, a wrong shared secret): how many requests it stands for, at
    /// least 1; 0 for a row of a session.
    /// </summary>
    public int Requests { get; init; }

    /// <summary>The address of a sender that is no switch (Add switch of the log row fills it in); null otherwise.</summary>
    public string? Sender { get; init; }

    /// <summary>The site the sender that is no switch reached (null: this server's network).</summary>
    public Guid? SenderSiteId { get; init; }
}

/// <summary>The texts of the log rows of dropped requests (docs/spec/radius-server.md "Log").</summary>
public static class DroppedRequestTexts
{
    public const string NotAdded = "Request from a switch that is not added. Add it under Switches.";
    public const string WrongSecret = "Wrong shared secret: the request's check value does not match.";

    /// <summary>"10.0.0.9 (not added)", at a site "10.0.0.9 at Building B (not added)".</summary>
    public static string Sender(string address, string? siteName) =>
        siteName is { Length: > 0 } ? $"{address} at {siteName} (not added)" : $"{address} (not added)";

    /// <summary>The reason with the count of a row that stands for more than one request.</summary>
    public static string Reason(string reason, int requests) =>
        requests > 1 ? string.Create(CultureInfo.InvariantCulture, $"{reason} {requests:N0} requests in the last minute.") : reason;
}

/// <param name="Total">Rows matching the search.</param>
public sealed record LogPage(int Total, IReadOnlyList<LogRow> Rows);

public sealed record LogEntryRef(long Id);

/// <summary>The client certificate of a log row (DER, base64): the page shows it in <c>ui:CertificateWindow</c>.</summary>
public sealed record LogCertificate(string Der);

/// <summary>An input error: the field (the DTO property in camelCase, "rules[2].vlan") and the message below it.</summary>
public sealed record RadiusFieldError(string Field, string Message);

/// <summary>
/// The input rules of the page, the same on the page (errors below the fields) and on the server (which refuses what breaks
/// them with an <see cref="ArgumentException"/> carrying the message).
/// </summary>
public static class RadiusRules
{
    public const string PortRange = "Use 1 to 65535.";
    public const string LogKeptRange = "Keep from 1,000 to 50,000 entries.";
    public const string EnterName = "Enter a name.";
    public const string NameTooLong = "Use at most 64 characters.";
    public const string NameTaken = "Another switch has this name.";
    public const string EnterAddress = "Enter an IP address or a host name.";
    public const string AddressTaken = "Another switch has this address.";
    public const string EnterSecret = "Enter a shared secret.";
    public const string SecretTooShort = "Use 8 characters or more.";
    public const string SecretTooLong = "Use at most 128 characters.";
    public const string SecretCharacters = "Use letters, digits and punctuation, no spaces.";
    public const string TooManySwitches = "At most 500 switches.";

    /// <summary>The first error of the settings; null when they are fine.</summary>
    public static RadiusFieldError? Validate(RadiusSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Port is < 1 or > 65535)
        {
            return new("port", PortRange);
        }

        return settings.LogKept is < RadiusLimits.MinLogKept or > RadiusLimits.MaxLogKept ? new("logKept", LogKeptRange) : null;
    }

    /// <summary>
    /// The first error of a switch; null when it is fine. <paramref name="others"/>: the other switches (name, site, address),
    /// <paramref name="isNew"/>: a new switch needs a secret.
    /// </summary>
    public static RadiusFieldError? Validate(SaveSwitchRequest request, IEnumerable<SwitchRow> others, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(others);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return new("name", EnterName);
        }

        if (name.Length > RadiusLimits.MaxNameLength)
        {
            return new("name", NameTooLong);
        }

        var address = request.Address?.Trim() ?? string.Empty;
        if (!IsAddress(address))
        {
            return new("address", EnterAddress);
        }

        var list = others.Where(o => o.Id != request.Id).ToList();
        if (list.Any(o => string.Equals(o.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            return new("name", NameTaken);
        }

        if (list.Any(o => o.SiteId == request.SiteId && SameAddress(o.Address, address)))
        {
            return new("address", AddressTaken);
        }

        var secret = request.Secret ?? string.Empty;
        if (secret.Length == 0)
        {
            return isNew ? new("secret", EnterSecret) : null;
        }

        if (secret.Length < RadiusLimits.MinSecretLength)
        {
            return new("secret", SecretTooShort);
        }

        if (secret.Length > RadiusLimits.MaxSecretLength)
        {
            return new("secret", SecretTooLong);
        }

        return secret.All(c => c is > ' ' and <= '~') ? null : new("secret", SecretCharacters);
    }

    /// <summary>An IP address, or a host name (letters, digits, hyphens and dots, labels of 1..63, at most 253).</summary>
    public static bool IsAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        var text = address.Trim();
        if (text.Contains(':', StringComparison.Ordinal))
        {
            return IPAddress.TryParse(text, out var v6) && v6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        }

        if (text.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            // Only a full dotted quad: .NET also reads "10" or "10.0.0" as IPv4 shorthand, which no switch is configured with.
            var parts = text.Split('.');
            return parts.Length == 4 && parts.All(p => p.Length is >= 1 and <= 3 && int.Parse(p, CultureInfo.InvariantCulture) <= 255);
        }

        var name = text.TrimEnd('.');
        return name.Length is > 0 and <= 253 && !name.All(c => char.IsAsciiDigit(c) || c == '.')
            && name.Split('.').All(label => label.Length is >= 1 and <= 63 && !label.StartsWith('-') && !label.EndsWith('-')
                && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
    }

    /// <summary>Two addresses are the same switch: equal IP addresses, or equal host names (case-insensitive).</summary>
    public static bool SameAddress(string? a, string? b)
    {
        var left = a?.Trim() ?? string.Empty;
        var right = b?.Trim() ?? string.Empty;
        if (IPAddress.TryParse(left, out var x) && IPAddress.TryParse(right, out var y))
        {
            return Normalize(x).Equals(Normalize(y));
        }

        return string.Equals(left.TrimEnd('.'), right.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An IPv4-mapped IPv6 address as IPv4.</summary>
    public static IPAddress Normalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }
}

/// <summary>JSON of the page calls, the events and the stored settings: camelCase, enums as camelCase strings.</summary>
public static class RadiusJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>The value of <paramref name="json"/>; an empty or unreadable request is an <see cref="ArgumentException"/>.</summary>
    public static T Deserialize<T>(string? json)
    {
        try
        {
            return (string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, Options))
                ?? throw new ArgumentException("The request is empty.", nameof(json));
        }
        catch (JsonException ex)
        {
            throw new ArgumentException("The request cannot be read.", nameof(json), ex);
        }
    }
}
