using System.Globalization;
using System.Text.Json;

using Oadm.Sdk.Actions;
using Oadm.Sdk.Compliance;

namespace Oadm.Plugins.Acap;

/// <summary>
/// Compliance check "Application running": the chosen application is installed (and running, at least a version, with a
/// valid license). Read only: one <c>applications/list.cgi</c> per device and run, shared by every check of this type.
/// </summary>
public sealed class ApplicationRunningCheck : IComplianceCheckType
{
    public const string TypeId = "oadm.acap.running";

    /// <summary>The key of the shared application list of a run.</summary>
    public const string ListKey = "oadm.acap:list";

    public string Id => TypeId;

    public string DisplayName => "Application running";

    public string? IconKey => "app";

    public string Group => "Applications";

    public string? Description => "The application is installed and running on the device.";

    public IReadOnlyList<ActionField> Fields =>
    [
        ActionField.DynamicChoice("application", "Application") with { ChoicesFromDevice = true, Hint = "Read the list from a device that has it." },
        ActionField.Choice("state", "Must be", [new ActionChoice("running", "Running"), new ActionChoice("installed", "Installed")]),
        ActionField.Text("minimumVersion", "At least version") with { Hint = "Optional", MaxLength = 32 },
        ActionField.Bool("license", "License must be valid"),
    ];

    public IReadOnlyDictionary<string, string> Validate(JsonElement settings)
    {
        var minimum = new ComplianceInvocation(Guid.Empty, string.Empty, settings).Get("minimumVersion");
        return string.IsNullOrWhiteSpace(minimum) || ParseVersion(minimum) is not null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["minimumVersion"] = "Enter a version like 1.4." };
    }

    public string Describe(JsonElement settings)
    {
        var invocation = new ComplianceInvocation(Guid.Empty, string.Empty, settings);
        var text = invocation.Get("application") + (invocation.Get("state") == "installed" ? " installed" : " running");
        if (!string.IsNullOrWhiteSpace(invocation.Get("minimumVersion")))
        {
            text += ", at least " + invocation.Get("minimumVersion")!.Trim();
        }

        return invocation.GetBool("license") ? text + ", license valid" : text;
    }

    public async Task<IReadOnlyList<ActionChoice>> ListChoicesAsync(IComplianceQueryContext ctx, string fieldKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (fieldKey != "application" || ctx.Vapix is null)
        {
            return [];
        }

        var applications = await new ApplicationApiClient(ctx.Vapix).ListAsync(ct).ConfigureAwait(false);
        return [.. applications
            .OrderBy(a => a.NiceName ?? a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new ActionChoice(a.Name, string.IsNullOrWhiteSpace(a.NiceName) ? a.Name : a.NiceName))];
    }

    public async Task<ComplianceResult> EvaluateAsync(IComplianceContext ctx, ComplianceInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        var name = invocation.Get("application") ?? string.Empty;
        var applications = await ctx.Reads.GetOrAddAsync(
            ListKey, token => new ApplicationApiClient(ctx.Vapix!).ListAsync(token), ct).ConfigureAwait(false);
        var app = applications.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
        if (app is null)
        {
            return ComplianceResult.Failed(name + " is not installed");
        }

        var label = string.IsNullOrWhiteSpace(app.NiceName) ? app.Name : app.NiceName;
        if (invocation.Get("state") != "installed" && !app.IsRunning)
        {
            return ComplianceResult.Failed(label + " is " + (string.IsNullOrWhiteSpace(app.Status) ? "not running" : app.Status.ToLowerInvariant()));
        }

        if (ParseVersion(invocation.Get("minimumVersion")) is { } minimum
            && (ParseVersion(app.Version) is not { } version || version < minimum))
        {
            return ComplianceResult.Failed(string.Create(CultureInfo.InvariantCulture, $"{label} {app.Version}, needs {invocation.Get("minimumVersion")!.Trim()}"));
        }

        if (invocation.GetBool("license") && !LicenseValid(app))
        {
            return ComplianceResult.Failed(label + ": license " + (string.IsNullOrWhiteSpace(app.License) ? "unknown" : app.License.ToLowerInvariant()));
        }

        return ComplianceResult.Passed($"{label} {app.Version} {(app.IsRunning ? "running" : "installed")}".Replace("  ", " ", StringComparison.Ordinal));
    }

    private static bool LicenseValid(InstalledApplication app)
    {
        if (app.NeedsNoLicense || string.Equals(app.License, "Custom", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.Equals(app.License, "Valid", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !DateTime.TryParse(app.LicenseExpirationDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires)
            || expires >= DateTime.UtcNow.Date;
    }

    /// <summary>The leading numbers of a version ("1.4.18-beta" -> 1.4.18); null when it has none.</summary>
    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var numeric = new string([.. text.Trim().TakeWhile(c => char.IsDigit(c) || c == '.')]).Trim('.');
        if (!numeric.Contains('.', StringComparison.Ordinal))
        {
            numeric += ".0";
        }

        return Version.TryParse(numeric, out var version) ? version : null;
    }
}
