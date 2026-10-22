namespace Oadm.Sdk.Licensing;

/// <summary>The state of the server's OADM PRO license.</summary>
public enum ProLicenseStatus
{
    /// <summary>No OADM PRO license is imported.</summary>
    None = 0,

    /// <summary>A valid license (or trial license) for this server.</summary>
    Active = 1,

    /// <summary>The license is past its last day.</summary>
    Expired = 2,

    /// <summary>The license was made for another server.</summary>
    WrongServer = 3,

    /// <summary>The license cannot be read or its signature does not match.</summary>
    Invalid = 4,
}

/// <summary>
/// The server's OADM PRO license as OADM checked it. OADM itself never depends on it: it only tells PRO plugins whether PRO
/// is active, the plan, the number of devices and the last day.
/// </summary>
/// <param name="Status">The state of the license.</param>
/// <param name="Reason">One plain sentence for the Settings page and the plugins' own messages.</param>
/// <param name="Customer">Who the license is for; null without a readable license.</param>
/// <param name="Plan">The plan, e.g. "Up to 500 devices"; null without a readable license.</param>
/// <param name="MaxDevices">The devices the plan covers; null = no limit or no readable license.</param>
/// <param name="Expires">The last day of the license; null = it never expires or no readable license.</param>
/// <param name="IsTrial">A 30-day trial license.</param>
public sealed record ProLicenseInfo(
    ProLicenseStatus Status,
    string Reason,
    string? Customer = null,
    string? Plan = null,
    long? MaxDevices = null,
    DateOnly? Expires = null,
    bool IsTrial = false)
{
    /// <summary>OADM PRO is active: a valid license or trial license for this server.</summary>
    public bool IsActive => Status == ProLicenseStatus.Active;

    /// <summary>The state without a license.</summary>
    public static ProLicenseInfo None { get; } = new(ProLicenseStatus.None, ProLicenseTexts.NoLicense);
}

/// <summary>The live OADM PRO license of the server (<c>ICorePluginContext.Pro</c>, <c>ITaskExecutionContext.Pro</c>).</summary>
public interface IProLicense
{
    /// <summary>The current state.</summary>
    ProLicenseInfo Current { get; }

    /// <summary>
    /// Raised after <see cref="Current"/> changed: a license was imported or removed, or it ran out. Runs on a server thread;
    /// keep the handler short.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>OADM PRO is active.</summary>
    bool IsActive => Current.IsActive;
}

/// <summary>The texts of the OADM PRO license, the same on the server, in the client and in the plugins.</summary>
public static class ProLicenseTexts
{
    /// <summary>The state without a license.</summary>
    public const string NoLicense = "No license";

    /// <summary>A license made for another server ID.</summary>
    public const string WrongServer = "This license is for another server.";

    /// <summary>An unknown signing key, changed content or another product.</summary>
    public const string NotValid = "This is not a valid OADM PRO license.";

    /// <summary>A PRO plugin cannot be turned on without an active license (Plugins page, server refusal).</summary>
    public const string NeedsPro = "Needs an OADM PRO license or trial.";

    /// <summary>"Expired on 2028-10-22."</summary>
    public static string ExpiredOn(DateOnly day) => $"Expired on {LicenseFile.Day(day)}.";

    /// <summary>"Up to 500 devices, until 2028-10-22" / "Trial, until 2026-11-21" (next to the label "OADM PRO").</summary>
    public static string Active(string? plan, DateOnly? expires, bool trial)
    {
        var until = expires is { } day ? $", until {LicenseFile.Day(day)}" : "";
        var name = trial ? "Trial" : string.IsNullOrWhiteSpace(plan) ? "Active" : plan;
        return name + until;
    }
}
