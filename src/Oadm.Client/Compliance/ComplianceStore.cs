using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Client.Devices;
using Oadm.Contracts.V1;

namespace Oadm.Client.Compliance;

/// <summary>How a device or a result counts on the Compliance page and in the Devices grid (worst wins).</summary>
public enum ComplianceLevel
{
    /// <summary>No result that counts (not checked, not applicable, could not check, or no check for the device).</summary>
    None = 0,

    /// <summary>Every check that counts passed.</summary>
    Passed = 1,

    /// <summary>A check with the severity Warning failed.</summary>
    Warning = 2,

    /// <summary>A check with the severity Problem failed.</summary>
    Problem = 3,
}

/// <summary>
/// The compliance of one device: its result per check, the worst level and the texts of the grid cell. One object per
/// device for the whole session (rows bind to it), updated in place; <see cref="ResultsChanged"/> tells the cells of the
/// per-check columns.
/// </summary>
public sealed partial class DeviceCompliance : ObservableObject
{
    private readonly Dictionary<string, ComplianceResultEntry> _results = new(StringComparer.Ordinal);

    internal DeviceCompliance(string deviceId) => DeviceId = deviceId;

    public string DeviceId { get; }

    [ObservableProperty] public partial ComplianceLevel Level { get; private set; }

    /// <summary>"2 problems, 1 warning", "OK", empty without results.</summary>
    [ObservableProperty] public partial string Text { get; private set; } = string.Empty;

    /// <summary>The failed checks, one per line ("Reachable: Unreachable"); null when none failed.</summary>
    [ObservableProperty] public partial string? ToolTip { get; private set; }

    /// <summary>Problems first, then warnings, then passed, then devices without results.</summary>
    [ObservableProperty] public partial int SortKey { get; private set; }

    public int Problems { get; private set; }

    public int Warnings { get; private set; }

    /// <summary>A result changed (cells of the per-check columns read it again).</summary>
    public event EventHandler? ResultsChanged;

    public ComplianceResultEntry? Get(string checkId) => _results.GetValueOrDefault(checkId);

    internal IEnumerable<ComplianceResultEntry> Results => _results.Values;

    internal void Set(ComplianceResultEntry entry)
    {
        if (entry.Removed)
        {
            _results.Remove(entry.CheckId);
        }
        else
        {
            _results[entry.CheckId] = entry;
        }
    }

    internal void ClearResults() => _results.Clear();

    /// <summary>The level of one result with its check (null = it does not count).</summary>
    public static ComplianceLevel LevelOf(ComplianceResultEntry? result, ComplianceCheckSummary? check) =>
        result is null || check is null || !check.Enabled || check.Problem.Length > 0
            ? ComplianceLevel.None
            : result.Outcome switch
            {
                ComplianceOutcome.CompliancePassed => ComplianceLevel.Passed,
                ComplianceOutcome.ComplianceFailed => check.Severity == ComplianceSeverity.ComplianceWarning ? ComplianceLevel.Warning : ComplianceLevel.Problem,
                _ => ComplianceLevel.None,
            };

    internal void Recompute(IReadOnlyDictionary<string, ComplianceCheckSummary> checks)
    {
        int problems = 0, warnings = 0, passed = 0;
        var failed = new List<string>();
        foreach (var result in _results.Values)
        {
            var check = checks.GetValueOrDefault(result.CheckId);
            switch (LevelOf(result, check))
            {
                case ComplianceLevel.Problem:
                    problems++;
                    failed.Add(check!.Name + ": " + FirstLine(result.Detail));
                    break;
                case ComplianceLevel.Warning:
                    warnings++;
                    failed.Add(check!.Name + ": " + FirstLine(result.Detail));
                    break;
                case ComplianceLevel.Passed:
                    passed++;
                    break;
            }
        }

        Problems = problems;
        Warnings = warnings;
        Level = problems > 0 ? ComplianceLevel.Problem : warnings > 0 ? ComplianceLevel.Warning : passed > 0 ? ComplianceLevel.Passed : ComplianceLevel.None;
        Text = Level switch
        {
            ComplianceLevel.None => string.Empty,
            ComplianceLevel.Passed => "OK",
            _ => string.Join(", ", new[] { Count(problems, "problem"), Count(warnings, "warning") }.Where(t => t.Length > 0)),
        };
        ToolTip = failed.Count == 0 ? null : string.Join("\n", failed.Order(StringComparer.CurrentCulture));
        SortKey = Level switch
        {
            ComplianceLevel.Problem => 3_000_000 + (problems * 1000) + warnings,
            ComplianceLevel.Warning => 2_000_000 + warnings,
            ComplianceLevel.Passed => 1_000_000,
            _ => 0,
        };
        ResultsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0];

    private static string Count(int n, string noun) =>
        n == 0 ? string.Empty : n == 1 ? "1 " + noun : string.Create(CultureInfo.CurrentCulture, $"{n:N0} {noun}s");
}

/// <summary>
/// The client's mirror of the compliance checks and their results (ComplianceService.WatchChecks / WatchResults), fed by
/// <see cref="Shell.ServerConnection"/> on the UI thread while the role may see compliance. Each change touches only the
/// devices it names; <see cref="ResultsChanged"/> lists them.
/// </summary>
public sealed class ComplianceStore
{
    private readonly Dictionary<string, DeviceCompliance> _devices = new(StringComparer.Ordinal);
    private Dictionary<string, ComplianceCheckSummary> _checks = new(StringComparer.Ordinal);

    /// <summary>The checks, the default ones first (server order).</summary>
    public IReadOnlyList<ComplianceCheckSummary> Checks { get; private set; } = [];

    /// <summary>True once the first list of checks arrived (the role may see compliance and the server has it).</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>The checks changed (names, counts, severity, turned on or off).</summary>
    public event EventHandler? ChecksChanged;

    /// <summary>Results of these devices changed (reset = every device).</summary>
    public event EventHandler<DeviceStoreChangedEventArgs>? ResultsChanged;

    public ComplianceCheckSummary? FindCheck(string checkId) => _checks.GetValueOrDefault(checkId);

    /// <summary>The compliance of a device (created on first use; stays the same object).</summary>
    public DeviceCompliance For(string deviceId)
    {
        if (!_devices.TryGetValue(deviceId, out var compliance))
        {
            compliance = new DeviceCompliance(deviceId);
            _devices[deviceId] = compliance;
        }

        return compliance;
    }

    public void ApplyChecks(ComplianceCheckList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        var levelsChanged = !IsAvailable || list.Checks.Count != _checks.Count || list.Checks.Any(c =>
            !_checks.TryGetValue(c.Id, out var old) || old.Severity != c.Severity || old.Enabled != c.Enabled || old.Problem != c.Problem || old.Name != c.Name);
        Checks = [.. list.Checks];
        _checks = list.Checks.ToDictionary(c => c.Id, StringComparer.Ordinal);
        IsAvailable = true;
        if (levelsChanged)
        {
            foreach (var device in _devices.Values)
            {
                device.Recompute(_checks);
            }

            ResultsChanged?.Invoke(this, DeviceStoreChangedEventArgs.ResetAll);
        }

        ChecksChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Results in stream order; <paramref name="reset"/>: these are every result (a new snapshot).</summary>
    public void ApplyResults(IReadOnlyList<ComplianceResultEntry> results, bool reset)
    {
        ArgumentNullException.ThrowIfNull(results);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        if (reset)
        {
            foreach (var device in _devices.Values)
            {
                if (device.Results.Any())
                {
                    device.ClearResults();
                    changed.Add(device.DeviceId);
                }
            }
        }

        foreach (var result in results)
        {
            For(result.DeviceId).Set(result);
            changed.Add(result.DeviceId);
        }

        foreach (var id in changed)
        {
            _devices[id].Recompute(_checks);
        }

        if (changed.Count > 0 || reset)
        {
            ResultsChanged?.Invoke(this, reset ? DeviceStoreChangedEventArgs.ResetAll : new DeviceStoreChangedEventArgs(false, changed));
        }
    }

    /// <summary>The role may no longer see compliance, or the connection ended.</summary>
    public void Clear()
    {
        if (!IsAvailable && _devices.Values.All(d => !d.Results.Any()))
        {
            return;
        }

        foreach (var device in _devices.Values)
        {
            device.ClearResults();
            device.Recompute(_checks);
        }

        Checks = [];
        _checks = new Dictionary<string, ComplianceCheckSummary>(StringComparer.Ordinal);
        IsAvailable = false;
        ChecksChanged?.Invoke(this, EventArgs.Empty);
        ResultsChanged?.Invoke(this, DeviceStoreChangedEventArgs.ResetAll);
    }
}
