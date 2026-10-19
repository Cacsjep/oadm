using Oadm.Sdk.Compliance;

namespace Oadm.Core.Compliance;

/// <summary>
/// A compliance check (table ComplianceChecks): a check type with its settings, its devices (<see cref="DevicesJson"/>, a
/// <c>DeviceSelector</c>), the severity of a failure and how often it reads the devices.
/// </summary>
public sealed class ComplianceCheckEntity
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string TypeId { get; set; } = string.Empty;

    public string SettingsJson { get; set; } = "{}";

    public string DevicesJson { get; set; } = "{}";

    public ComplianceSeverity Severity { get; set; }

    /// <summary>Minutes between two reads of a device; 0 for types that use cached data only.</summary>
    public int IntervalMinutes { get; set; }

    public bool Enabled { get; set; }

    /// <summary>One of the default checks (Reachable, Login works): always on, not deletable.</summary>
    public bool BuiltIn { get; set; }

    /// <summary>Raised by every save; a save of an older version is refused (two users).</summary>
    public int Version { get; set; }

    public DateTime CreatedUtc { get; set; }

    public DateTime ChangedUtc { get; set; }
}

/// <summary>The result of one check on one device (table ComplianceResults, key CheckId + DeviceId).</summary>
public sealed class ComplianceResultEntity
{
    public const int MaxDetailLength = 1000;

    public Guid CheckId { get; set; }

    public Guid DeviceId { get; set; }

    public ComplianceOutcome Outcome { get; set; }

    public string Detail { get; set; } = string.Empty;

    public DateTime CheckedUtc { get; set; }

    /// <summary>Since when the outcome holds.</summary>
    public DateTime SinceUtc { get; set; }
}
