using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Contracts.Vulnerabilities;

namespace Oadm.Client.Vulnerabilities;

/// <summary>
/// The "Vulnerabilities" window of the selected devices (context menu, docs/spec/vulnerabilities.md): every CVE once, worst
/// first, with the level, score, fixed versions, the full description and a link to Axis' advisory; with several devices also
/// how many it affects and which (tooltip). A snapshot taken when it opens. The devices are grouped by their shared
/// <see cref="DeviceVulnerabilities"/> first (one per model and firmware): 100,000 selected devices cost one pass.
/// </summary>
public sealed partial class DeviceVulnerabilitiesViewModel : ObservableObject
{
    /// <summary>Devices named in the tooltip of the Devices column; the rest is counted.</summary>
    public const int MaxDeviceNames = 12;

    private readonly IUrlLauncher? _launcher;

    public DeviceVulnerabilitiesViewModel(IReadOnlyList<DeviceRowViewModel> devices, IUrlLauncher? launcher = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        _launcher = launcher;
        ShowDevices = devices.Count > 1;

        int affected = 0;
        var groups = new Dictionary<DeviceVulnerabilities, List<DeviceRowViewModel>>(ReferenceEqualityComparer.Instance);
        foreach (var device in devices)
        {
            var found = device.Vulnerabilities;
            if (!found.Any)
            {
                continue;
            }

            affected++;
            if (!groups.TryGetValue(found, out var members))
            {
                groups[found] = members = [];
            }

            members.Add(device);
        }

        var perCve = new Dictionary<string, Aggregate>(StringComparer.Ordinal);
        foreach (var (found, members) in groups)
        {
            foreach (var item in found.Items)
            {
                if (!perCve.TryGetValue(item.Vulnerability.Id, out var aggregate))
                {
                    perCve[item.Vulnerability.Id] = aggregate = new Aggregate(item.Vulnerability);
                }

                aggregate.Add(item.FixedIn, members);
            }
        }

        Rows = [.. perCve.Values
            .OrderByDescending(a => a.Vulnerability.SeverityRank)
            .ThenByDescending(a => a.Vulnerability.Score ?? 0)
            .ThenByDescending(a => a.Count)
            .ThenByDescending(a => a.Vulnerability.Id, StringComparer.Ordinal)
            .Select(a => new VulnerabilityRowViewModel(a.Vulnerability, a.FixedInText(), a.Count, ShowDevices ? a.DevicesTip() : null, OpenAdvisoryCommand))];
        CountText = Rows.Count == 1 ? "1 CVE" : string.Create(CultureInfo.InvariantCulture, $"{Rows.Count:N0} CVEs");
        DeviceText = devices.Count == 1
            ? Label(devices[0])
            : string.Create(CultureInfo.InvariantCulture, $"{affected:N0} of {devices.Count:N0} selected devices");
    }

    /// <summary>One device: "M3086-V · 10.0.0.48 · 11.11.93"; several: "3 of 5 selected devices".</summary>
    public string DeviceText { get; }

    public string CountText { get; }

    /// <summary>Several devices: the Devices column (how many each CVE affects, which in its tooltip).</summary>
    public bool ShowDevices { get; }

    public IReadOnlyList<VulnerabilityRowViewModel> Rows { get; }

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Opens the advisory in the browser (https links only).</summary>
    [RelayCommand]
    private async Task OpenAdvisoryAsync(VulnerabilityRowViewModel? row)
    {
        if (_launcher is not null && row?.AdvisoryUri is { } uri)
        {
            await _launcher.OpenAsync(uri).ConfigureAwait(true);
        }
    }

    /// <summary>"M3086-V · 10.0.0.48 · 11.11.93".</summary>
    private static string Label(DeviceRowViewModel device) =>
        string.Join(" · ", new[] { device.Model, device.DisplayAddress, device.FirmwareVersion }.Where(t => !string.IsNullOrWhiteSpace(t)));

    /// <summary>One CVE over the selected devices: the fixed versions, the count and the first names.</summary>
    private sealed class Aggregate(Vulnerability vulnerability)
    {
        private readonly HashSet<string> _fixedIn = new(StringComparer.Ordinal);
        private readonly List<DeviceRowViewModel> _named = [];
        private bool _noFix;

        public Vulnerability Vulnerability { get; } = vulnerability;

        public int Count { get; private set; }

        public void Add(string? fixedIn, List<DeviceRowViewModel> devices)
        {
            if (fixedIn is null)
            {
                _noFix = true;
            }
            else
            {
                _fixedIn.Add(fixedIn);
            }

            Count += devices.Count;
            _named.AddRange(devices.Take(MaxDeviceNames - _named.Count));
        }

        /// <summary>"6.50.5.19, 11.11.93", lowest first; "Not listed" when a device's track has none.</summary>
        public string FixedInText()
        {
            var versions = _fixedIn.Order(Comparer<string>.Create(static (a, b) =>
                FirmwareVersion.TryParse(a, out var x) && FirmwareVersion.TryParse(b, out var y) ? x.CompareTo(y) : string.CompareOrdinal(a, b)));
            return string.Join(", ", _noFix ? versions.Append(VulnerabilityRowViewModel.NotListed) : versions);
        }

        public string DevicesTip()
        {
            var lines = _named.Select(Label);
            return string.Join(Environment.NewLine, Count > _named.Count
                ? lines.Append(string.Create(CultureInfo.InvariantCulture, $"and {Count - _named.Count:N0} more"))
                : lines);
        }
    }
}

/// <summary>One CVE of the window.</summary>
public sealed class VulnerabilityRowViewModel
{
    /// <summary>The fixed version when the device's track has none.</summary>
    public const string NotListed = "Not listed";

    public VulnerabilityRowViewModel(Vulnerability vulnerability, string fixedIn, int deviceCount = 1, string? devicesTip = null,
        IAsyncRelayCommand<VulnerabilityRowViewModel?>? openAdvisory = null)
    {
        ArgumentNullException.ThrowIfNull(vulnerability);
        ArgumentNullException.ThrowIfNull(fixedIn);
        var v = vulnerability;
        Id = v.Id;
        Level = v.Severity.Length > 0 ? v.Severity : "Unrated";
        IsSevere = v.SeverityRank >= 3;
        IsModerate = v.SeverityRank == 2;
        IsMinor = v.SeverityRank <= 1;
        Score = v.Score;
        ScoreText = v.Score is { } score ? score.ToString("0.0", CultureInfo.InvariantCulture) : "";
        SortKey = (v.SeverityRank * 1000) + (int)Math.Round((v.Score ?? 0) * 10);
        FixedIn = fixedIn.Length > 0 ? fixedIn : NotListed;
        DeviceCount = deviceCount;
        DeviceCountText = deviceCount.ToString("N0", CultureInfo.InvariantCulture);
        DevicesTip = devicesTip;
        Description = v.Description;
        AdvisoryUri = Uri.TryCreate(v.Advisory, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
        OpenAdvisory = openAdvisory;
    }

    public string Id { get; }

    /// <summary>"Critical", "High", "Medium", "Low" or "Unrated".</summary>
    public string Level { get; }

    public bool IsSevere { get; }

    public bool IsModerate { get; }

    public bool IsMinor { get; }

    public double? Score { get; }

    public string ScoreText { get; }

    /// <summary>The level, then the score (sorting the Level column).</summary>
    public int SortKey { get; }

    /// <summary>The first fixed version of each affected device's track ("6.50.5.19, 11.11.93"), or "Not listed".</summary>
    public string FixedIn { get; }

    /// <summary>How many of the selected devices it affects.</summary>
    public int DeviceCount { get; }

    public string DeviceCountText { get; }

    /// <summary>The affected devices, one per line, at most <see cref="DeviceVulnerabilitiesViewModel.MaxDeviceNames"/>; null for one device.</summary>
    public string? DevicesTip { get; }

    public string Description { get; }

    public Uri? AdvisoryUri { get; }

    public bool HasAdvisory => AdvisoryUri is not null;

    public IAsyncRelayCommand<VulnerabilityRowViewModel?>? OpenAdvisory { get; }
}
