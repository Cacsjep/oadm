using Oadm.Sdk.Vapix;

namespace Oadm.Core.Vapix;

/// <summary>
/// The API list of AXIS OS before 8.50, which has no <c>apidiscovery.cgi</c> (404). The same facts come from the
/// <c>param.cgi</c> group <c>Properties</c>; only APIs whose old form OADM's plugins use are listed. Verified on an AXIS
/// Q6154-E with AXIS OS 8.40.104: <c>Properties.FirmwareManagement.Version=1.0</c> with a working firmwaremanagement.cgi
/// status, applications/list.cgi (<c>Properties.EmbeddedDevelopment.Version=2.16</c>), pwdgrp.cgi
/// (<c>Properties.API.Browser.UserGroup=yes</c>); no time.cgi, ntp.cgi or systemready.cgi; network_settings.cgi without
/// getNetworkInfo (so not listed: the plugins use their param.cgi fallback).
/// </summary>
public static class LegacyApiList
{
    /// <summary>Marks the entries as derived, in <see cref="DeviceApi.Status"/>.</summary>
    public const string Source = "from parameters";

    public static readonly string[] Groups = ["Properties"];

    public const string ApplicationId = "application";

    /// <summary>APIs VAPIX identifies by a parameter: added from <see cref="Groups"/> when the API list leaves them out.</summary>
    public static readonly string[] PropertyIds = ["param-cgi", ApplicationId, "user-management"];

    /// <summary>
    /// The Application API 1.0 when <c>Properties.EmbeddedDevelopment.Version</c> exists (how VAPIX identifies it), else null.
    /// </summary>
    public static DeviceApi? ApplicationFrom(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return (parameters.TryGetValue("Properties.EmbeddedDevelopment.Version", out var value) || parameters.TryGetValue("root.Properties.EmbeddedDevelopment.Version", out value))
            && !string.IsNullOrWhiteSpace(value)
            ? new DeviceApi(ApplicationId, "1.0", "Application", Source)
            : null;
    }

    public static IReadOnlyList<DeviceApi> FromProperties(IReadOnlyDictionary<string, string> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        string? Get(string name) =>
            parameters.TryGetValue(name, out var value) || parameters.TryGetValue("root." + name, out value) ? value?.Trim() : null;

        // param.cgi answered, so the legacy parameter API is there.
        var list = new List<DeviceApi> { new("param-cgi", "1.0", "Legacy Parameter Handling", Source) };
        if (Get("Properties.FirmwareManagement.Version") is { Length: > 0 } fwmgr && System.Version.TryParse(fwmgr, out _))
        {
            list.Add(new DeviceApi("fwmgr", fwmgr, "Firmware Management", Source));
        }

        if (ApplicationFrom(parameters) is { } application)
        {
            list.Add(application);
        }

        if (string.Equals(Get("Properties.API.Browser.UserGroup"), "yes", StringComparison.OrdinalIgnoreCase))
        {
            list.Add(new DeviceApi("user-management", "1.0", "User Management", Source));
        }

        return list;
    }
}
