using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VapixCommander;

public enum CompatibilityState
{
    Compatible = 0,

    /// <summary>The device does not list the API at all.</summary>
    MissingApi = 1,

    /// <summary>The device lists the API with a lower version (or another major version).</summary>
    VersionTooOld = 2,

    /// <summary>The API list of the device has not been read yet (no full refresh so far).</summary>
    Unknown = 3,

    /// <summary>The command needs a video device.</summary>
    NoVideo = 4,
}

public sealed record CompatibilityResult(CompatibilityState State, string Text)
{
    public bool IsCompatible => State == CompatibilityState.Compatible;
}

/// <summary>
/// Checks a command's <c>requires</c> against a device's API list (cached for the picker, fresh before the first
/// write, CLAUDE.md "device safety"). VAPIX semantics: same major version, at least the minimum minor version.
/// </summary>
public static class Compatibility
{
    public static CompatibilityResult Check(CommandDefinition command, IReadOnlyList<DeviceApi> apis, bool hasVideo = true)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(apis);
        if (command.HasVideoOnly && !hasVideo)
        {
            return new(CompatibilityState.NoVideo, "Needs a video device");
        }

        if (apis.Count == 0)
        {
            return new(CompatibilityState.Unknown, "API list not read yet");
        }

        foreach (var requirement in command.Requires)
        {
            if (!Version.TryParse(requirement.MinVersion, out var required))
            {
                return new(CompatibilityState.Unknown, $"Invalid minimum version {requirement.MinVersion} of {requirement.Api}");
            }

            var sameMajor = apis.FindApi(requirement.Api, required.Major);
            if (sameMajor is null)
            {
                var other = apis.FindApi(requirement.Api);
                return other is null
                    ? new(CompatibilityState.MissingApi, $"Missing API {requirement.Api}")
                    : new(CompatibilityState.VersionTooOld, $"Has {requirement.Api} {other.Version}, needs {requirement.MinVersion}");
            }

            if (sameMajor.ParsedVersion < required)
            {
                return new(CompatibilityState.VersionTooOld, $"Version too old: {requirement.Api} {sameMajor.Version}, needs {requirement.MinVersion}");
            }
        }

        return new(CompatibilityState.Compatible, "Compatible");
    }

    /// <summary>Throws <see cref="DeviceNotCompatibleException"/> ("Nothing was changed") unless every requirement is met.</summary>
    public static void Require(CommandDefinition command, IReadOnlyList<DeviceApi> apis)
    {
        ArgumentNullException.ThrowIfNull(command);
        foreach (var requirement in command.Requires)
        {
            apis.Require(requirement.Api, requirement.MinVersion);
        }
    }
}
