namespace Oadm.Sdk.Vapix;

/// <summary>
/// One entry of the device's VAPIX API discovery list (<c>apidiscovery.cgi getApiList</c>),
/// e.g. <c>user-management 1.2</c>, <c>network-settings 1.37</c>, <c>fwmgr 1.10</c>.
/// A device can list the same API id with several major versions.
/// </summary>
public sealed record DeviceApi(string Id, string Version, string? Name = null, string? Status = null)
{
    /// <summary>Parsed <see cref="Version"/>; 0.0 when the device reports something unparsable.</summary>
    public Version ParsedVersion => System.Version.TryParse(Version, out var v) ? v : new Version(0, 0);
}

/// <summary>
/// Compatibility checks every task plugin must use before it changes anything on a device.
/// Semantics follow VAPIX versioning: a different major version is a different, incompatible API;
/// within a major version a higher minor version is backwards compatible.
/// </summary>
public static class DeviceApiExtensions
{
    /// <summary>The highest listed version of <paramref name="apiId"/> with the given major version, or of any major when null.</summary>
    public static DeviceApi? FindApi(this IEnumerable<DeviceApi> apis, string apiId, int? major = null)
    {
        ArgumentNullException.ThrowIfNull(apis);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiId);
        return apis
            .Where(a => string.Equals(a.Id, apiId, StringComparison.OrdinalIgnoreCase))
            .Where(a => major is null || a.ParsedVersion.Major == major)
            .OrderByDescending(a => a.ParsedVersion)
            .FirstOrDefault();
    }

    /// <summary>True when the device offers <paramref name="apiId"/> with the same major version and at least <paramref name="minVersion"/>.</summary>
    public static bool Supports(this IEnumerable<DeviceApi> apis, string apiId, string minVersion)
    {
        var required = System.Version.Parse(minVersion);
        var found = apis.FindApi(apiId, required.Major);
        return found is not null && found.ParsedVersion >= required;
    }

    /// <summary>Throws <see cref="DeviceNotCompatibleException"/> unless <see cref="Supports"/> is true.</summary>
    public static DeviceApi Require(this IEnumerable<DeviceApi> apis, string apiId, string minVersion)
    {
        var list = apis as IReadOnlyCollection<DeviceApi> ?? apis.ToList();
        var required = System.Version.Parse(minVersion);
        var found = list.FindApi(apiId, required.Major);
        if (found is null || found.ParsedVersion < required)
        {
            throw new DeviceNotCompatibleException(apiId, minVersion, list.FindApi(apiId)?.Version);
        }

        return found;
    }
}

/// <summary>The device does not offer a required VAPIX API version. Nothing was changed on the device.</summary>
public sealed class DeviceNotCompatibleException : Exception
{
    public DeviceNotCompatibleException(string apiId, string requiredVersion, string? foundVersion)
        : base(foundVersion is null
            ? $"Device does not support {apiId} (needs {requiredVersion} or later). Nothing was changed."
            : $"Device has {apiId} {foundVersion}, needs {requiredVersion} or later. Nothing was changed.")
    {
        ApiId = apiId;
        RequiredVersion = requiredVersion;
        FoundVersion = foundVersion;
    }

    public DeviceNotCompatibleException()
    {
        ApiId = RequiredVersion = string.Empty;
    }

    public DeviceNotCompatibleException(string message)
        : base(message)
    {
        ApiId = RequiredVersion = string.Empty;
    }

    public DeviceNotCompatibleException(string message, Exception innerException)
        : base(message, innerException)
    {
        ApiId = RequiredVersion = string.Empty;
    }

    public string ApiId { get; }

    public string RequiredVersion { get; }

    public string? FoundVersion { get; }
}
