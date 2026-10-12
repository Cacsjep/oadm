using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Sdk.Devices;

namespace Oadm.Plugins.EdgeRecordingExport.Client;

/// <summary>One chosen camera in the grid: what the search found and how its download goes.</summary>
public sealed partial class CameraRowViewModel : ObservableObject
{
    public CameraRowViewModel(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        Device = device;
        Model = device.Model?.Replace("AXIS ", string.Empty, StringComparison.Ordinal) ?? string.Empty;
        AddressSortKey = AddressKey(device.Address);
    }

    public IDeviceInfo Device { get; }

    public string Address => Device.Address;

    public string Model { get; }

    /// <summary>Sorts IPv4 addresses numerically, host names after them.</summary>
    public string AddressSortKey { get; }

    /// <summary>The search result (null while waiting or searching).</summary>
    public SearchResult? Result { get; private set; }

    public IReadOnlyList<RecordingEntry> Recordings => Result?.Recordings ?? [];

    public int RecordingCount => Recordings.Count;

    public long? EstimatedBytes => Result?.EstimatedBytes;

    public string RecordingsText => Result is { Problem: null } ? RecordingCount.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;

    public string SizeText => Result is { Problem: null } r ? (r.Recordings.Count == 0 ? string.Empty : EdgeRecordingFormat.AboutSize(r.EstimatedBytes)) : string.Empty;

    public long SizeSortKey => EstimatedBytes ?? -1;

    public DateTimeOffset? First => Recordings.Count == 0 ? null : Recordings[0].Start;

    public DateTimeOffset? Last => Recordings.Count == 0 ? null : Recordings.Max(r => r.Stop ?? DateTimeOffset.MaxValue);

    public string FirstText => First is { } f ? EdgeRecordingFormat.Local(f) : string.Empty;

    public string LastText => Last is { } l ? (l == DateTimeOffset.MaxValue ? "Recording now" : EdgeRecordingFormat.Local(l)) : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusSortKey))]
    public partial string StatusText { get; set; } = "Waiting";

    [ObservableProperty]
    public partial string? StatusDetail { get; set; }

    [ObservableProperty]
    public partial bool IsOk { get; set; }

    [ObservableProperty]
    public partial bool IsWarning { get; set; }

    [ObservableProperty]
    public partial bool IsError { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public string StatusSortKey => StatusText;

    /// <summary>Recordings of this camera downloaded, skipped (already there) and failed in the current download.</summary>
    public int Downloaded { get; set; }

    public int Skipped { get; set; }

    public int Failed { get; set; }

    public void SetWaiting()
    {
        Result = null;
        Status("Waiting");
        RaiseResult();
    }

    public void SetSearching() => Status("Searching", busy: true);

    public void SetNotChecked() => Status("Not checked");

    public void SetResult(SearchResult result)
    {
        Result = result;
        RaiseResult();
        if (result.Problem is { } problem)
        {
            Status(problem, error: true);
        }
        else if (result.Recordings.Count == 0)
        {
            Status(result.NoDisk ? "No SD card or network share" : "No recordings");
        }
        else if (result.Truncated)
        {
            Status(EdgeRecordingFormat.Count(result.Recordings.Count, "recording") + " found", warning: true,
                detail: string.Create(CultureInfo.CurrentCulture, $"Only the first {EdgeRecordingInfo.MaxRecordingsPerCamera:N0} are listed. Choose a shorter range."));
        }
        else
        {
            Status(EdgeRecordingFormat.Count(result.Recordings.Count, "recording") + " found", ok: true);
        }
    }

    public void BeginDownload()
    {
        Downloaded = 0;
        Skipped = 0;
        Failed = 0;
    }

    public void SetDownloading(int index) =>
        Status(string.Create(CultureInfo.CurrentCulture, $"Downloading {index:N0} of {RecordingCount:N0}"), busy: true, detail: StatusDetail);

    /// <summary>The result of this camera's download: "20 downloaded", "18 downloaded, 2 failed", "Stopped".</summary>
    public void EndDownload(bool stopped, string? lastError)
    {
        var parts = new List<string>();
        if (Downloaded > 0 || (Skipped == 0 && Failed == 0))
        {
            parts.Add(Downloaded.ToString("N0", CultureInfo.CurrentCulture) + " downloaded");
        }

        if (Skipped > 0)
        {
            parts.Add(Skipped.ToString("N0", CultureInfo.CurrentCulture) + " already there");
        }

        if (Failed > 0)
        {
            parts.Add(Failed.ToString("N0", CultureInfo.CurrentCulture) + " failed");
        }

        var done = Downloaded + Skipped + Failed;
        if (stopped && done < RecordingCount)
        {
            parts.Add("stopped");
        }

        var text = string.Join(", ", parts);
        text = char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
        Status(text, ok: Failed == 0 && !stopped, warning: Failed == 0 && stopped, error: Failed > 0, detail: Failed > 0 ? lastError : null);
    }

    private void Status(string text, bool ok = false, bool warning = false, bool error = false, bool busy = false, string? detail = null)
    {
        StatusText = text;
        StatusDetail = detail;
        IsOk = ok;
        IsWarning = warning;
        IsError = error;
        IsBusy = busy;
    }

    private void RaiseResult()
    {
        OnPropertyChanged(nameof(RecordingCount));
        OnPropertyChanged(nameof(RecordingsText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SizeSortKey));
        OnPropertyChanged(nameof(FirstText));
        OnPropertyChanged(nameof(LastText));
    }

    private static string AddressKey(string address) =>
        System.Net.IPAddress.TryParse(address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? "0" + string.Concat(ip.GetAddressBytes().Select(b => b.ToString("D3", CultureInfo.InvariantCulture)))
            : "1" + address;
}
