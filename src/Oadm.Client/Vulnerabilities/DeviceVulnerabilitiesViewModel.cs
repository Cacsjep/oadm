using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Contracts.Vulnerabilities;

namespace Oadm.Client.Vulnerabilities;

/// <summary>
/// The "Vulnerabilities" window of the selected devices (context menu, docs/spec/vulnerabilities.md): every CVE once per kind,
/// worst first, the possible ones last, with the level, score, fixed versions, the full description and a link to Axis' advisory; with several devices also
/// how many it affects and which (tooltip). A snapshot taken when it opens. The devices are grouped by their shared
/// <see cref="DeviceVulnerabilities"/> first (one per model, firmware and chip): 100,000 selected devices cost one pass.
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

        // One row per CVE and kind: a CVE certain on some devices and possible on others has a row for each.
        var perCve = new Dictionary<(string Id, bool Possible), Aggregate>();
        foreach (var (found, members) in groups)
        {
            foreach (var item in found.Items)
            {
                if (!perCve.TryGetValue((item.Vulnerability.Id, item.Possible), out var aggregate))
                {
                    perCve[(item.Vulnerability.Id, item.Possible)] = aggregate = new Aggregate(item.Vulnerability, item.Possible);
                }

                aggregate.Add(item.FixedIn, members);
            }
        }

        Rows = [.. perCve.Values
            .OrderBy(a => a.Possible)
            .ThenByDescending(a => a.Vulnerability.SeverityRank)
            .ThenByDescending(a => a.Vulnerability.Score ?? 0)
            .ThenByDescending(a => a.Count)
            .ThenByDescending(a => a.Vulnerability.Id, StringComparer.Ordinal)
            .Select(a => new VulnerabilityRowViewModel(a.Vulnerability, a.FixedInText(), a.Count, ShowDevices ? a.DevicesTip() : null,
                OpenAdvisoryCommand, a.Possible))];
        var certain = perCve.Keys.Where(k => !k.Possible).Select(k => k.Id).ToHashSet(StringComparer.Ordinal);
        int possible = perCve.Keys.Count(k => k.Possible && !certain.Contains(k.Id));
        var possibleText = string.Create(CultureInfo.InvariantCulture, $"{possible:N0} possible");
        CountText = certain.Count == 0 && possible > 0 ? possibleText
            : (certain.Count == 1 ? "1 CVE" : string.Create(CultureInfo.InvariantCulture, $"{certain.Count:N0} CVEs"))
                + (possible > 0 ? ", " + possibleText : "");
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

    /// <summary>One CVE (certain or possible) over the selected devices: the fixed versions, the count and the first names.</summary>
    private sealed class Aggregate(Vulnerability vulnerability, bool possible)
    {
        private readonly HashSet<string> _fixedIn = new(StringComparer.Ordinal);
        private readonly List<DeviceRowViewModel> _named = [];
        private bool _noFix;

        public Vulnerability Vulnerability { get; } = vulnerability;

        public bool Possible { get; } = possible;

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

        /// <summary>
        /// "6.50.5.19, 11.11.93", lowest first; "Not listed" when a device's track has none; a possible one "11.11 (check the
        /// advisory for your LTS)".
        /// </summary>
        public string FixedInText()
        {
            var versions = _fixedIn.Order(Comparer<string>.Create(static (a, b) =>
                FirmwareVersion.TryParse(a, out var x) && FirmwareVersion.TryParse(b, out var y) ? x.CompareTo(y) : string.CompareOrdinal(a, b)));
            var text = string.Join(", ", versions);
            if (Possible && text.Length > 0)
            {
                text = DeviceVulnerabilities.FixedText(text, possible: true);
            }

            return _noFix ? (text.Length > 0 ? text + ", " : "") + VulnerabilityRowViewModel.NotListed : text;
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

    /// <param name="possible">A possible match (<see cref="DeviceVulnerability.Possible"/>): level "Possibly affected", muted.</param>
    public VulnerabilityRowViewModel(Vulnerability vulnerability, string fixedIn, int deviceCount = 1, string? devicesTip = null,
        IAsyncRelayCommand<VulnerabilityRowViewModel?>? openAdvisory = null, bool possible = false)
    {
        ArgumentNullException.ThrowIfNull(vulnerability);
        ArgumentNullException.ThrowIfNull(fixedIn);
        var v = vulnerability;
        Id = v.Id;
        IsPossible = possible;
        Level = possible ? DeviceVulnerabilities.PossibleLevel : v.Severity.Length > 0 ? v.Severity : "Unrated";
        IsSevere = !possible && v.SeverityRank >= 3;
        IsModerate = !possible && v.SeverityRank == 2;
        IsMinor = !possible && v.SeverityRank <= 1;
        Score = v.Score;
        ScoreText = v.Score is { } score ? score.ToString("0.0", CultureInfo.InvariantCulture) : "";
        SortKey = (possible ? 0 : 10_000) + (v.SeverityRank * 1000) + (int)Math.Round((v.Score ?? 0) * 10);
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

    /// <summary>A possible match: the level muted, after the certain ones.</summary>
    public bool IsPossible { get; }

    public double? Score { get; }

    public string ScoreText { get; }

    /// <summary>Certain before possible, then the level, then the score (sorting the Level column).</summary>
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
