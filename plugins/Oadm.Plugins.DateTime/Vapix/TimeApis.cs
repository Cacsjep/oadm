using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.DateAndTime.Vapix;

/// <summary>
/// VAPIX APIs of the plugin and the minimum version per method (decision table in the plugin README).
/// Sources: developer.axis.com Time API (time-service, AXIS OS 9.30+), NTP API (ntp, AXIS OS 9.10+), System settings
/// (param.cgi Time.*). The NTP API has no per-version history; NTS is used from the version verified on AXIS OS 12.11.
/// </summary>
public static class TimeApis
{
    public const string TimeService = "time-service";
    public const string Ntp = "ntp";
    public const string ParamCgi = "param-cgi";

    public const string TimePath = "axis-cgi/time.cgi";
    public const string NtpPath = "axis-cgi/ntp.cgi";
    public const string ParamPath = "axis-cgi/param.cgi";

    /// <summary>getDateTimeInfo, setDateTime, setTimeZone (IANA), setPosixTimeZone (posixTimeZone, enableDst).</summary>
    public const string TimeServiceBase = "1.0";

    /// <summary>getNTPInfo, setNTPClientConfiguration (enabled, serversSource, staticServers).</summary>
    public const string NtpBase = "1.0";

    /// <summary>NTSEnabled and staticNTSKEServers (verified with ntp 1.5 on AXIS OS 12.11; no version history published).</summary>
    public const string NtpNts = "1.5";

    /// <summary>Legacy Time.* parameters (time zone, NTP) for devices without time-service / ntp.</summary>
    public const string ParamCgiBase = "1.0";

    /// <summary>The device offers something this plugin can configure (CanRun on the cached list).</summary>
    public static bool CanConfigure(IEnumerable<DeviceApi> apis)
    {
        var list = apis as IReadOnlyCollection<DeviceApi> ?? apis.ToList();
        return list.Supports(TimeService, TimeServiceBase) || list.Supports(ParamCgi, ParamCgiBase);
    }

    public static bool HasTimeService(IEnumerable<DeviceApi> apis) => apis.Supports(TimeService, TimeServiceBase);

    public static bool HasNtpApi(IEnumerable<DeviceApi> apis) => apis.Supports(Ntp, NtpBase);

    public static bool SupportsNts(IEnumerable<DeviceApi> apis) => apis.Supports(Ntp, NtpNts);

    /// <summary>"time-service 1.1, ntp 1.5", "param.cgi" or a mix.</summary>
    public static string Describe(IReadOnlyCollection<DeviceApi> apis)
    {
        var time = apis.FindApi(TimeService, 1) is { } t && HasTimeService(apis) ? $"time-service {t.Version}" : "param.cgi";
        var ntp = apis.FindApi(Ntp, 1) is { } n && HasNtpApi(apis) ? $"ntp {n.Version}" : "param.cgi";
        return time == ntp ? time : $"{time}, {ntp}";
    }
}
