using System.Globalization;
using System.Text.Json;

using Oadm.Sdk.Actions;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Compliance;

/// <summary>The host's own check types: decided from what the server knows about a device, never contacting it.</summary>
public static class BuiltInComplianceTypes
{
    public const string SourceName = "Built in";

    public const string ReachableId = "oadm.compliance.reachable";
    public const string LoginWorksId = "oadm.compliance.login-works";
    public const string CertificateId = "oadm.compliance.certificate";
    public const string FirmwareId = "oadm.compliance.firmware";

    /// <summary>Fixed ids of the default checks, created at the first start.</summary>
    public static readonly Guid ReachableCheckId = new("6d0a7c52-6f8e-4c4b-9a51-0c0a3b1f0001");

    public static readonly Guid LoginWorksCheckId = new("6d0a7c52-6f8e-4c4b-9a51-0c0a3b1f0002");

    public static readonly IReadOnlyList<IComplianceCheckType> All =
        [new ReachableType(), new LoginWorksType(), new CertificateType(), new FirmwareType()];

    /// <summary>The default checks: id, name, type.</summary>
    public static readonly IReadOnlyList<(Guid Id, string Name, string TypeId)> Defaults =
        [(ReachableCheckId, "Reachable", ReachableId), (LoginWorksCheckId, "Login works", LoginWorksId)];

    /// <summary>The numbers of an AXIS OS version ("12.11.77" -> 12, 11, 77); null when it is not one.</summary>
    public static int[]? ParseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var parts = version.Trim().Split('.');
        if (parts.Length is < 2 or > 6)
        {
            return null;
        }

        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }

        return numbers;
    }

    /// <summary>Compares two parsed versions part by part; missing parts count as 0.</summary>
    public static int CompareVersions(int[] a, int[] b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y)
            {
                return x.CompareTo(y);
            }
        }

        return 0;
    }

    private abstract class CachedType : IComplianceCheckType
    {
        public abstract string Id { get; }

        public abstract string DisplayName { get; }

        public abstract string IconKey { get; }

        public string Group => "Devices";

        public abstract string? Description { get; }

        public virtual IReadOnlyList<ActionField> Fields => [];

        public bool UsesCachedDataOnly => true;

        public virtual ComplianceSeverity DefaultSeverity => ComplianceSeverity.Problem;

        public virtual IReadOnlyDictionary<string, string> Validate(JsonElement settings) => new Dictionary<string, string>();

        public virtual string Describe(JsonElement settings) => DisplayName;

        public Task<ComplianceResult> EvaluateAsync(IComplianceContext ctx, ComplianceInvocation invocation, CancellationToken ct) =>
            Task.FromResult(Evaluate(ctx.Device, invocation, ctx.Now));

        protected abstract ComplianceResult Evaluate(IDeviceInfo device, ComplianceInvocation invocation, DateTimeOffset now);
    }

    private sealed class ReachableType : CachedType
    {
        public override string Id => ReachableId;

        public override string DisplayName => "Reachable";

        public override string IconKey => "network";

        public override string? Description => "The device answers OADM.";

        protected override ComplianceResult Evaluate(IDeviceInfo device, ComplianceInvocation invocation, DateTimeOffset now) =>
            device.Status == DeviceStatus.Unreachable ? ComplianceResult.Failed("Unreachable") : ComplianceResult.Passed();
    }

    private sealed class LoginWorksType : CachedType
    {
        public override string Id => LoginWorksId;

        public override string DisplayName => "Login works";

        public override string IconKey => "key";

        public override string? Description => "OADM can log in to the device.";

        protected override ComplianceResult Evaluate(IDeviceInfo device, ComplianceInvocation invocation, DateTimeOffset now) => device.Status switch
        {
            DeviceStatus.CredentialsRequired => ComplianceResult.Failed("Credentials required"),
            DeviceStatus.PasswordNotSet => ComplianceResult.Failed("Password not set"),
            DeviceStatus.CertificateChanged => ComplianceResult.Failed("Certificate changed"),
            DeviceStatus.Unreachable => new ComplianceResult(ComplianceOutcome.NotChecked, "Unreachable"),
            _ => ComplianceResult.Passed(),
        };
    }

    private sealed class CertificateType : CachedType
    {
        public override string Id => CertificateId;

        public override string DisplayName => "Certificate";

        public override string IconKey => "shield";

        public override string? Description => "The HTTPS certificate of the device is valid and trusted.";

        private static readonly string[] Reasons = ["expired", "expiresSoon", "notTrusted"];

        public override ComplianceSeverity DefaultSeverity => ComplianceSeverity.Warning;

        public override IReadOnlyList<ActionField> Fields =>
        [
            ActionField.Bool("expired", "Fail when expired", defaultValue: true),
            ActionField.Bool("expiresSoon", "Fail when it expires soon", defaultValue: true),
            ActionField.Number("days", "Days left at least", 1, 3650, 30) with { VisibleWhenKey = "expiresSoon", VisibleWhenValue = "true" },
            ActionField.Bool("notTrusted", "Fail when not trusted (self-signed or unknown issuer)"),
        ];

        public override IReadOnlyDictionary<string, string> Validate(JsonElement settings)
        {
            var on = Reasons
                .Any(k => settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True);
            return on ? new Dictionary<string, string>() : new Dictionary<string, string> { [string.Empty] = "Choose at least one reason to fail." };
        }

        public override string Describe(JsonElement settings)
        {
            var invocation = new ComplianceInvocation(Guid.Empty, string.Empty, settings);
            var parts = new List<string>();
            if (invocation.GetBool("expired"))
            {
                parts.Add("not expired");
            }

            if (invocation.GetBool("expiresSoon"))
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"at least {invocation.GetInt("days") ?? 30} days left"));
            }

            if (invocation.GetBool("notTrusted"))
            {
                parts.Add("trusted");
            }

            return "Certificate " + string.Join(", ", parts);
        }

        protected override ComplianceResult Evaluate(IDeviceInfo device, ComplianceInvocation invocation, DateTimeOffset now)
        {
            if (device.HttpsEnabled == false)
            {
                return ComplianceResult.NotApplicable("HTTPS is off");
            }

            if (device.CertNotAfterUtc is not { } notAfter)
            {
                return new ComplianceResult(ComplianceOutcome.NotChecked, "The certificate was not read yet");
            }

            var expired = notAfter <= now.UtcDateTime;
            var daysLeft = (int)Math.Floor((notAfter - now.UtcDateTime).TotalDays);
            if (invocation.GetBool("expired") && expired)
            {
                return ComplianceResult.Failed("Expired");
            }

            if (invocation.GetBool("expiresSoon") && daysLeft < (invocation.GetInt("days") ?? 30))
            {
                return ComplianceResult.Failed(expired ? "Expired" : DaysText(daysLeft));
            }

            if (invocation.GetBool("notTrusted") && device.CertTrustName is "SelfSigned" or "Untrusted")
            {
                return ComplianceResult.Failed(device.CertTrustName == "SelfSigned" ? "Self-signed" : "Untrusted");
            }

            return ComplianceResult.Passed(notAfter.Year >= 9999 ? "Never expires" : expired ? "Expired" : DaysText(daysLeft));
        }

        private static string DaysText(int days) => days switch
        {
            <= 0 => "Expires today",
            1 => "1 day left",
            _ => string.Create(CultureInfo.InvariantCulture, $"{days:N0} days left"),
        };
    }

    private sealed class FirmwareType : CachedType
    {
        public override string Id => FirmwareId;

        public override string DisplayName => "Firmware";

        public override string IconKey => "chip";

        public override string? Description => "The device runs at least the AXIS OS version you set.";

        public override IReadOnlyList<ActionField> Fields =>
            [ActionField.Text("minimum", "At least AXIS OS", required: true) with { Hint = "For example 12.5.56", MaxLength = 32 }];

        public override IReadOnlyDictionary<string, string> Validate(JsonElement settings) =>
            ParseVersion(new ComplianceInvocation(Guid.Empty, string.Empty, settings).Get("minimum")) is null
                ? new Dictionary<string, string> { ["minimum"] = "Enter a version like 12.5.56." }
                : new Dictionary<string, string>();

        public override string Describe(JsonElement settings) =>
            "AXIS OS " + new ComplianceInvocation(Guid.Empty, string.Empty, settings).Get("minimum")?.Trim() + " or later";

        protected override ComplianceResult Evaluate(IDeviceInfo device, ComplianceInvocation invocation, DateTimeOffset now)
        {
            var minimum = ParseVersion(invocation.Get("minimum"));
            if (minimum is null)
            {
                return ComplianceResult.CouldNotCheck("The check has no valid version");
            }

            var version = ParseVersion(device.FirmwareVersion);
            if (version is null)
            {
                return new ComplianceResult(ComplianceOutcome.NotChecked, "The AXIS OS version is not known yet");
            }

            return CompareVersions(version, minimum) >= 0
                ? ComplianceResult.Passed("AXIS OS " + device.FirmwareVersion)
                : ComplianceResult.Failed("AXIS OS " + device.FirmwareVersion + ", needs " + invocation.Get("minimum")!.Trim());
        }
    }
}
