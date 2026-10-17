namespace Oadm.Sdk.Licensing;

/// <summary>
/// The <c>license</c> entry of a plugin's plugin.json: the plugin is sold with a license. Without this entry the plugin is
/// free and OADM asks for no license.
/// </summary>
/// <param name="PublicKey">
/// The vendor's public key (base64 of the SubjectPublicKeyInfo of an ECDSA P-256 key, as <c>manage license keys</c>
/// writes it). Only licenses signed with the matching private key are accepted.
/// </param>
/// <param name="Features">The features a license can unlock, for the Plugins page and the vendor's signing tool.</param>
/// <param name="TrialDays">
/// Days the plugin runs without a license, counted from the day the server first loaded it; 0 = no trial.
/// </param>
public sealed record PluginLicenseManifest(string PublicKey, IReadOnlyList<string>? Features = null, int TrialDays = 0);

/// <summary>What a license file says, signed by the plugin's vendor.</summary>
/// <param name="Plugin">The plugin id (plugin.json <c>id</c>) the license is for.</param>
/// <param name="ServerId">The server the license is for (the server ID on the Plugins page).</param>
/// <param name="Customer">Who bought it, shown on the Plugins page.</param>
/// <param name="Issued">The day the vendor made the license.</param>
/// <param name="Expires">The last day the license is valid; null = it never expires.</param>
/// <param name="Features">The features it unlocks; empty = every feature of the plugin.</param>
/// <param name="Limits">Numbers the plugin enforces itself, for example "devices" = 500.</param>
public sealed record LicenseData(
    string Plugin,
    string ServerId,
    string Customer,
    DateOnly Issued,
    DateOnly? Expires,
    IReadOnlyList<string> Features,
    IReadOnlyDictionary<string, long> Limits);

/// <summary>The state of a plugin's license.</summary>
public enum LicenseStatus
{
    /// <summary>The plugin needs no license (no <c>license</c> entry in plugin.json).</summary>
    NotRequired = 0,

    /// <summary>A valid license for this plugin and this server.</summary>
    Valid = 1,

    /// <summary>No license yet, but the plugin's trial is running.</summary>
    Trial = 2,

    /// <summary>No license and the trial is over.</summary>
    TrialEnded = 3,

    /// <summary>No license and the plugin has no trial.</summary>
    Missing = 4,

    /// <summary>The license is past its last day.</summary>
    Expired = 5,

    /// <summary>The license was made for another server.</summary>
    WrongServer = 6,

    /// <summary>The license cannot be read, is for another plugin or its signature does not match.</summary>
    Invalid = 7,
}

/// <summary>A plugin's license as OADM checked it: the status and, for a readable license, what it allows.</summary>
/// <param name="Status">The state of the license.</param>
/// <param name="Reason">One plain sentence for the Plugins page and the plugin's own messages.</param>
/// <param name="Data">The license's content when it could be read and its signature matches; null otherwise.</param>
/// <param name="TrialEnds">The last day of the trial while there is one; null otherwise.</param>
public sealed record PluginLicenseInfo(LicenseStatus Status, string Reason, LicenseData? Data = null, DateOnly? TrialEnds = null)
{
    /// <summary>The plugin may run: a valid license, a running trial, or no license needed.</summary>
    public bool IsValid => Status is LicenseStatus.Valid or LicenseStatus.Trial or LicenseStatus.NotRequired;

    /// <summary>
    /// Whether the license unlocks <paramref name="feature"/>: always when no license is needed or during the trial; with
    /// a valid license when it lists the feature or lists none (= every feature); never otherwise.
    /// </summary>
    public bool Has(string feature) => Status switch
    {
        LicenseStatus.NotRequired or LicenseStatus.Trial => true,
        LicenseStatus.Valid => Data is { } data && (data.Features.Count == 0 || data.Features.Contains(feature, StringComparer.OrdinalIgnoreCase)),
        _ => false,
    };

    /// <summary>
    /// The limit <paramref name="name"/> of a valid license; null when it sets none, no license is needed or the trial
    /// runs (no limit); 0 when the plugin may not run.
    /// </summary>
    public long? Limit(string name) => Status switch
    {
        LicenseStatus.Valid => Data?.Limits.FirstOrDefault(l => string.Equals(l.Key, name, StringComparison.OrdinalIgnoreCase)) is { Key: not null } limit ? limit.Value : null,
        LicenseStatus.NotRequired or LicenseStatus.Trial => null,
        _ => 0,
    };

    /// <summary>The state of a plugin that needs no license.</summary>
    public static PluginLicenseInfo NotRequired { get; } = new(LicenseStatus.NotRequired, "No license needed.");
}

/// <summary>
/// The live license of the plugin (<c>ICorePluginContext.License</c>, <c>ITaskExecutionContext.License</c>,
/// <c>IActionContext.License</c>): the current state, updated when an administrator enters or removes a license and when
/// it or the trial runs out. OADM only checks; the plugin decides what to do without a valid license.
/// </summary>
public interface IPluginLicense
{
    /// <summary>The current state.</summary>
    PluginLicenseInfo Current { get; }

    /// <summary>Raised after <see cref="Current"/> changed. Runs on a server thread; keep the handler short.</summary>
    event EventHandler? Changed;

    /// <summary>The plugin may run: a valid license, a running trial, or no license needed.</summary>
    bool IsValid => Current.IsValid;

    /// <summary>Whether the license unlocks <paramref name="feature"/> (see <see cref="PluginLicenseInfo.Has"/>).</summary>
    bool Has(string feature) => Current.Has(feature);

    /// <summary>The limit <paramref name="name"/> (see <see cref="PluginLicenseInfo.Limit"/>).</summary>
    long? Limit(string name) => Current.Limit(name);
}
