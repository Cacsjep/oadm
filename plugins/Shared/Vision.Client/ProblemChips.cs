using Oadm.Sdk.Devices;
using Oadm.Sdk.Sites;

namespace Oadm.Plugins.Shared.Vision.Client;

/// <summary>
/// What a plugin's own problem texts are, for <see cref="ProblemChips.Of"/>: its watching limits, the texts of a camera that
/// gives no or the wrong pictures, waiting for the graphics card, a camera outside the device group, a plugin not installed
/// completely, and the chip of a text the page does not know.
/// </summary>
public sealed record ProblemTexts
{
    public required Func<string, bool> IsWatchingLimit { get; init; }

    public IReadOnlyCollection<string> StreamErrors { get; init; } = [];

    public string? Starting { get; init; }

    public string? Off { get; init; }

    public IReadOnlyCollection<string> NotInstalled { get; init; } = [];

    /// <summary>The chip of a problem the page does not know (a newer server): "Not reading", "Not running".</summary>
    public required string Unknown { get; init; }
}

/// <summary>
/// The short red chip of a reader's or pipeline's problem in the list (docs/spec/ocr.md "Page", docs/spec/vision-center.md
/// "Page"); the server's whole text is its tooltip. The device statuses keep the names of the device list.
/// </summary>
public static class ProblemChips
{
    /// <summary>Beyond the watching limits (of the server or of a site).</summary>
    public const string LimitReached = "Limit reached";

    /// <summary>The camera does not give the pictures asked for (resolution, video source, no pictures).</summary>
    public const string StreamError = "Stream error";

    /// <summary>The camera is no longer in the device group.</summary>
    public const string NotInDeviceGroup = "Not in device group";

    /// <summary>Waiting for the graphics card's test.</summary>
    public const string Starting = "Starting";

    /// <summary>The plugin is not installed completely (or not running).</summary>
    public const string NotInstalled = "Not installed";

    /// <summary>The camera does not answer: the device grid's status name.</summary>
    public static readonly string NotReachable = DeviceMessages.StatusText(DeviceStatus.Unreachable);

    /// <summary>The camera refuses the stored login: the device grid's status name.</summary>
    public static readonly string LoginRefused = DeviceMessages.StatusText(DeviceStatus.CredentialsRequired);

    /// <summary>The chip of <paramref name="problem"/> with the plugin's own <paramref name="texts"/>.</summary>
    public static string Of(string problem, ProblemTexts texts)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(texts);
        if (texts.IsWatchingLimit(problem))
        {
            return LimitReached;
        }

        if (problem.StartsWith(SiteTexts.SiteOffline(null), StringComparison.Ordinal))
        {
            return SiteTexts.SiteOffline(null);
        }

        if (texts.StreamErrors.Contains(problem))
        {
            return StreamError;
        }

        if (problem == texts.Starting)
        {
            return Starting;
        }

        if (problem == texts.Off)
        {
            return NotInDeviceGroup;
        }

        if (texts.NotInstalled.Contains(problem))
        {
            return NotInstalled;
        }

        return problem switch
        {
            DeviceMessages.CredentialsRequired or DeviceMessages.PasswordNotSet or DeviceMessages.BasicOnlyOverHttp
                or DeviceMessages.Unauthorized or DeviceMessages.Forbidden => LoginRefused,
            DeviceMessages.CertificateChanged => DeviceMessages.StatusText(DeviceStatus.CertificateChanged),
            DeviceMessages.TimedOut or DeviceMessages.NotAnswering or DeviceMessages.HostUnreachable => NotReachable,
            DeviceMessages.NotFound or DeviceMessages.BadRequest => StreamError,
            _ when problem.StartsWith(DeviceMessages.Unreachable(string.Empty), StringComparison.Ordinal)
                || problem.StartsWith("Timeout", StringComparison.Ordinal) => NotReachable,
            _ when problem.StartsWith(DeviceMessages.ServerError(500)[..^3], StringComparison.Ordinal) => StreamError,
            _ => texts.Unknown,
        };
    }
}
