using System.Net;
using System.Text;

using Oadm.Plugins.Acap;
using Oadm.Plugins.DateAndTime.Model;
using Oadm.Plugins.DateAndTime.Vapix;
using Oadm.Plugins.HardeningScan.Checks;
using Oadm.Plugins.Pki.Device;
using Oadm.Plugins.Users;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Vapix;

using SnapshotRequests = Oadm.Plugins.SnapshotReport.SnapshotRequests;

namespace Oadm.Plugins.HardeningScan.Device;

/// <summary>
/// Reads what the checks of one level need from one device, strictly read-only: <c>param.cgi action=list</c> (one call, every
/// group of both levels), <c>pwdgrp.cgi action=get</c>, <c>GET /config/discover</c> and the <c>config/rest</c> GETs of the
/// APIs it lists, <c>ntp.cgi getNTPInfo</c>, <c>disks/list.cgi</c>, <c>applications/list.cgi</c> + <c>config.cgi action=get</c>,
/// and (Extended) the SOAP <c>GetWebServerTlsConfiguration</c>. Reads are planned from the level and the device's API list;
/// one failed read makes only its checks Error. When the first read (param.cgi) cannot reach the device or is refused, the
/// other reads are skipped with the same text.
/// </summary>
public sealed class DeviceFactsReader
{
    /// <summary>The param.cgi groups of every check (missing groups only add an error line to the answer).</summary>
    public static readonly IReadOnlyList<string> ParameterGroups =
    [
        "API.RemoteSyslog1",
        "Audio",
        "HTTPS",
        "Image.I0.MPEG.SignedVideo",
        "Image.I1.MPEG.SignedVideo",
        "Image.I2.MPEG.SignedVideo",
        "Image.I3.MPEG.SignedVideo",
        "Network.BootProto",
        "Network.Bonjour",
        "Network.Filter",
        "Network.Interface.I0.dot1x",
        "Network.RTSPS",
        "Network.SSH",
        "Network.UPnP",
        "Network.ZeroConf",
        "Properties.Audio",
        "Properties.Firmware",
        "Properties.HTTPS",
        "Properties.LocalStorage",
        "RemoteService",
        "SNMP",
        "Storage",
        "System.AccessLog",
        "System.PreventDoSAttack",
        "System.WebInterfaceDisabled",
        "Time",
        "WebService.DiscoveryMode",
    ];

    public const string ParamPath = "axis-cgi/param.cgi";
    public const string DiscoverPath = "config/discover";
    public const string DisksPath = "axis-cgi/disks/list.cgi?diskid=all";

    private readonly TimeSpan _requestTimeout;
    private readonly TimeProvider _time;

    public DeviceFactsReader(TimeSpan? requestTimeout = null, TimeProvider? time = null)
    {
        _requestTimeout = requestTimeout ?? HardeningScanPluginInfo.RequestTimeout;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The param.cgi list request of the scan (read-only).</summary>
    public static string ParameterUri => ParamPath + "?action=list&group=" + string.Join(',', ParameterGroups.Select(Uri.EscapeDataString));

    public async Task<DeviceFacts> ReadAsync(IVapixClient vapix, IDeviceInfo device, ScanLevel level, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(vapix);
        ArgumentNullException.ThrowIfNull(device);
        var now = _time.GetUtcNow();

        var parameters = await ReadAsync(ct2 => ReadParametersAsync(vapix, ct2), ct).ConfigureAwait(false);
        if (parameters.Error is { } unreachable && IsDeviceLevel(unreachable))
        {
            // The device does not answer or refuses the stored account: every other read would fail the same way.
            return new DeviceFacts
            {
                Device = device,
                Now = now,
                Params = parameters,
                Users = Fact.Failed<IReadOnlyList<DeviceUser>>(unreachable),
                PasswordPolicy = Fact.Failed<string>(unreachable),
                Time = Fact.Failed<CurrentTimeSettings>(unreachable),
                Disks = Fact.Failed<IReadOnlyList<DiskInfo>>(unreachable),
                Applications = Fact.Failed<IReadOnlyList<InstalledApplication>>(unreachable),
                Firewall = Fact.Failed<FirewallInfo>(unreachable),
                Snmp = Fact.Failed<SnmpInfo>(unreachable),
                OidcProvider = Fact.Failed<string>(unreachable),
                WebServerTls = Fact.Failed<WebServerTlsConfiguration>(unreachable),
            };
        }

        var apis = device.Apis;
        if (apis.Count == 0)
        {
            // Not refreshed yet: one fresh API list (a failure leaves the API-based reads not available).
            var fresh = await ReadAsync(ct2 => vapix.GetApiListAsync(ct2), ct).ConfigureAwait(false);
            apis = fresh.IsOk ? fresh.Value : [];
        }

        var discover = await ReadAsync(ct2 => ReadDiscoverAsync(vapix, ct2), ct).ConfigureAwait(false);
        var rest = discover.IsOk ? discover.Value : new Dictionary<string, RestApi>();
        bool HasRest(string name, int major) => rest.ContainsKey(DeviceParsers.Key(name, major));

        var users = await ReadAsync(ct2 => ReadUsersAsync(vapix, ct2), ct).ConfigureAwait(false);

        var policy = HasRest("user-management", 2)
            ? await ReadAsync(async ct2 => DeviceParsers.ParsePasswordPolicy(await GetJsonAsync(vapix, "config/rest/user-management/v2", ct2).ConfigureAwait(false)) ?? string.Empty, ct).ConfigureAwait(false)
            : Fact.NotAvailable<string>("No password policy setting on this firmware");

        Fact<CurrentTimeSettings> time;
        if (TimeApis.HasNtpApi(apis))
        {
            time = await ReadAsync(ct2 => TimeClient.ReadNtpInfoAsync(vapix, apis, new CurrentTimeSettings(), ct2), ct).ConfigureAwait(false);
        }
        else if (parameters.IsOk && parameters.Value.WithPrefix("Time.").Any())
        {
            time = Fact.Ok<CurrentTimeSettings>(TimeParsers.ParseParameters(parameters.Value.Values, new CurrentTimeSettings()));
        }
        else
        {
            time = parameters.Error is { } e ? Fact.Failed<CurrentTimeSettings>(e) : Fact.NotAvailable<CurrentTimeSettings>("No time settings on this device");
        }

        var hasStorage = apis.Supports("disk-management", "1.0")
            || (parameters.IsOk && parameters.Value.Bool("Properties.LocalStorage.SDCard") == true);
        var disks = hasStorage
            ? await ReadAsync(async ct2 => DeviceParsers.ParseDisks(await GetTextAsync(vapix, HttpMethod.Get, DisksPath, null, null, ct2).ConfigureAwait(false)), ct).ConfigureAwait(false)
            : Fact.NotAvailable<IReadOnlyList<DiskInfo>>("No edge storage on this device");

        Fact<IReadOnlyList<InstalledApplication>> applications;
        bool? allowUnsigned = null;
        if (apis.Supports("application", "1.0"))
        {
            var client = new ApplicationApiClient(vapix);
            applications = await ReadAsync(ct2 => client.ListAsync(ct2), ct).ConfigureAwait(false);
            if (applications.IsOk)
            {
                var allow = await ReadAsync(client.TryGetAllowUnsignedAsync, ct).ConfigureAwait(false);
                allowUnsigned = allow.IsOk ? allow.Value : null;
            }
        }
        else
        {
            applications = Fact.NotAvailable<IReadOnlyList<InstalledApplication>>("No applications on this device");
        }

        var firewall = HasRest("firewall", 1)
            ? await ReadAsync(async ct2 => DeviceParsers.ParseFirewall(await GetJsonAsync(vapix, "config/rest/firewall/v1", ct2).ConfigureAwait(false)), ct).ConfigureAwait(false)
            : Fact.NotAvailable<FirewallInfo>("No firewall on this firmware");

        bool? lldp = null;
        if (HasRest("lldp", 1))
        {
            var read = await ReadAsync(async ct2 => DeviceParsers.ParseLldpActivated(await GetJsonAsync(vapix, "config/rest/lldp/v1", ct2).ConfigureAwait(false)), ct).ConfigureAwait(false);
            lldp = read.IsOk ? read.Value : null;
        }

        var snmp = Fact.NotAvailable<SnmpInfo>(DeviceFacts.NotRead);
        var oidc = Fact.NotAvailable<string>(DeviceFacts.NotRead);
        var webServer = Fact.NotAvailable<WebServerTlsConfiguration>(DeviceFacts.NotRead);
        if (level == ScanLevel.Extended)
        {
            snmp = HasRest("snmp", 1)
                ? await ReadAsync(async ct2 => DeviceParsers.ParseSnmp(await GetJsonAsync(vapix, "config/rest/snmp/v1", ct2).ConfigureAwait(false)), ct).ConfigureAwait(false)
                : Fact.NotAvailable<SnmpInfo>("No SNMP API (the parameters are used)");
            oidc = HasRest("oidcsetup", 1)
                ? await ReadAsync(async ct2 => DeviceParsers.ParseOidcProvider(await GetJsonAsync(vapix, "config/rest/oidcsetup/v1", ct2).ConfigureAwait(false)) ?? string.Empty, ct).ConfigureAwait(false)
                : Fact.NotAvailable<string>("Needs AXIS OS 11.6 or later");
            webServer = await ReadWebServerAsync(vapix, ct).ConfigureAwait(false);
        }

        return new DeviceFacts
        {
            Device = device,
            Now = now,
            Apis = apis,
            Params = parameters,
            Users = users,
            PasswordPolicy = policy,
            Time = time,
            Disks = disks,
            Applications = applications,
            AllowUnsigned = allowUnsigned,
            Firewall = firewall,
            LldpActivated = lldp,
            Snmp = snmp,
            OidcProvider = oidc,
            WebServerTls = webServer,
        };
    }

    /// <summary>A failure that concerns the whole device (not reachable, refused account, timeout), not one API.</summary>
    internal static bool IsDeviceLevel(string error) =>
        error.StartsWith("Unreachable", StringComparison.Ordinal)
        || error.StartsWith("Timeout", StringComparison.Ordinal)
        || error.StartsWith("Unauthorized", StringComparison.Ordinal)
        || error.StartsWith("Forbidden", StringComparison.Ordinal)
        || error.StartsWith(DeviceMessages.Removed, StringComparison.Ordinal);

    private async Task<Fact<T>> ReadAsync<T>(Func<CancellationToken, Task<T>> read, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_requestTimeout);
        try
        {
            return Fact.Ok<T>(await read(timeout.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (NotAvailableException ex)
        {
            return Fact.NotAvailable<T>(ex.Message);
        }
#pragma warning disable CA1031 // One failed read marks only its checks: every failure becomes a short text.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Fact.Failed<T>(ErrorText(ex, _requestTimeout));
        }
    }

    /// <summary>The short text of a failed read, like the snapshot report ("Timeout after 15 s", "Unauthorized - HTTP 401").</summary>
    public static string ErrorText(Exception ex, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex switch
        {
            DeviceAnswerException answer => answer.Message,
            UnauthorizedAccessException => DeviceMessages.Unauthorized,
            PkiDeviceException { HttpStatus: 401 } => DeviceMessages.Unauthorized,
            FormatException or UserManagementException or AcapDeviceException or TimeApiException or PkiDeviceException => ex.Message,
            _ => SnapshotRequests.ExceptionError(ex, timeout),
        };
    }

    private static async Task<ParamList> ReadParametersAsync(IVapixClient vapix, CancellationToken ct) =>
        ParamList.Parse(await GetTextAsync(vapix, HttpMethod.Get, ParameterUri, null, null, ct).ConfigureAwait(false));

    private static async Task<IReadOnlyDictionary<string, RestApi>> ReadDiscoverAsync(IVapixClient vapix, CancellationToken ct)
    {
        try
        {
            return DeviceParsers.ParseDiscover(await GetTextAsync(vapix, HttpMethod.Get, DiscoverPath, null, null, ct).ConfigureAwait(false));
        }
        catch (DeviceAnswerException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return new Dictionary<string, RestApi>(); // older AXIS OS: no REST APIs
        }
    }

    private static async Task<IReadOnlyList<DeviceUser>> ReadUsersAsync(IVapixClient vapix, CancellationToken ct)
    {
        using var request = PwdgrpApi.BuildGet();
        return PwdgrpApi.ParseUsers(await SendForTextAsync(vapix, request, ct).ConfigureAwait(false));
    }

    private async Task<Fact<WebServerTlsConfiguration>> ReadWebServerAsync(IVapixClient vapix, CancellationToken ct)
    {
        var read = await ReadAsync(ct2 => WebServerTls.GetAsync(vapix, ct2), ct).ConfigureAwait(false);
        // Firmware without the SOAP web server service answers 404 or a SOAP fault for an unknown action.
        return read.Error is { } error && (error.Contains("HTTP 404", StringComparison.Ordinal) || error.Contains("ActionNotSupported", StringComparison.OrdinalIgnoreCase))
            ? Fact.NotAvailable<WebServerTlsConfiguration>("No web server settings service on this firmware")
            : read;
    }

    private static Task<string> GetJsonAsync(IVapixClient vapix, string path, CancellationToken ct) =>
        GetTextAsync(vapix, HttpMethod.Get, path, null, null, ct);

    private static async Task<string> GetTextAsync(IVapixClient vapix, HttpMethod method, string path, string? contentType, string? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType ?? "application/json");
        }

        return await SendForTextAsync(vapix, request, ct).ConfigureAwait(false);
    }

    private static async Task<string> SendForTextAsync(IVapixClient vapix, HttpRequestMessage request, CancellationToken ct)
    {
        request.Options.Set(VapixRequestOptions.Timeout, HardeningScanPluginInfo.RequestTimeout);
        using var response = await vapix.SendAsync(request, ct).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(bytes);
        if (!response.IsSuccessStatusCode)
        {
            throw new DeviceAnswerException(response.StatusCode, SnapshotRequests.HttpError(response.StatusCode, response.Content.Headers.ContentType?.MediaType, text.Length < 64 * 1024 ? text : null));
        }

        return text;
    }
}

/// <summary>The device answered with an HTTP error; the message is the short user text.</summary>
public sealed class DeviceAnswerException : Exception
{
    public DeviceAnswerException()
    {
    }

    public DeviceAnswerException(string message)
        : base(message)
    {
    }

    public DeviceAnswerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DeviceAnswerException(HttpStatusCode status, string message)
        : base(message)
    {
        Status = status;
    }

    public HttpStatusCode Status { get; }
}

/// <summary>The device does not have what a read needs; the check does not apply.</summary>
public sealed class NotAvailableException : Exception
{
    public NotAvailableException()
    {
    }

    public NotAvailableException(string message)
        : base(message)
    {
    }

    public NotAvailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
