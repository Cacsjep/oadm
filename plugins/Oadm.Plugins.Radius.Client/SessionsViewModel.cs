using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.Radius.Api;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.Radius.Client;

/// <summary>Texts of the page, one phrase each.</summary>
public static class RadiusTexts
{
    /// <summary>"2026-10-24 14:03:12" in local time.</summary>
    public static string Time(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>"0:42", "12:05", "1:02:03".</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{duration.Minutes}:{duration.Seconds:00}");
    }

    /// <summary>"1 session", "12 sessions".</summary>
    public static string Count(int count, string one, string many) =>
        count == 1 ? "1 " + one : string.Create(CultureInfo.CurrentCulture, $"{count:N0} {many}");
}

/// <summary>One open (or just ended) authentication of the Sessions tab, updated in place by the live events.</summary>
public sealed partial class SessionRowViewModel : ObservableObject
{
    private readonly TimeProvider _time;
    private DateTime _receivedUtc;

    internal SessionRowViewModel(SessionRow source, TimeProvider time)
    {
        _time = time;
        Source = source;
        _receivedUtc = time.GetUtcNow().UtcDateTime;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Switch), nameof(Port), nameof(Mac), nameof(Identity), nameof(Device), nameof(IsGuess), nameof(IpAddress),
        nameof(State), nameof(IsOk), nameof(IsError), nameof(IsRunning), nameof(IsEnded), nameof(Duration))]
    public partial SessionRow Source { get; set; }

    public Guid Id => Source.Id;

    public string Started => RadiusTexts.Time(Source.StartedUtc);

    public DateTime StartedSortKey => Source.StartedUtc;

    public string Switch => Source.SwitchName;

    /// <summary>"GigabitEthernet1/0/5", with the port type when the switch sent one.</summary>
    public string Port => Source.PortType is { Length: > 0 } type && Source.Port.Length > 0 ? $"{Source.Port} ({type})" : Source.Port;

    public string Mac => Source.Mac;

    public string Identity => Source.Identity ?? string.Empty;

    /// <summary>The device the certificate named, or before the check the device whose serial is the MAC (a guess).</summary>
    public string Device => Source.DeviceName;

    /// <summary>The device is a guess from the MAC: the grid shows it in italics.</summary>
    public bool IsGuess => Source.DeviceGuess && Device.Length > 0;

    public string IpAddress => Source.IpAddress;

    /// <summary>"Sending server certificate: fragment 2 of 4", "Authenticated with VLAN 20", "Failed: ...".</summary>
    public string State => Source.StateText.Length > 0 ? Source.StateText : Source.State;

    public bool IsOk => Source.State == SessionStates.Authenticated;

    public bool IsError => Source.State is SessionStates.Failed or SessionStates.TimedOut or SessionStates.Abandoned;

    public bool IsRunning => !IsEnded;

    public bool IsEnded => Source.Removed || Source.State is SessionStates.Authenticated or SessionStates.Failed or SessionStates.TimedOut or SessionStates.Abandoned;

    /// <summary>The time since the start; an open session's grows every second.</summary>
    public string Duration => RadiusTexts.Duration(DurationValue);

    public TimeSpan DurationValue => TimeSpan.FromMilliseconds(Source.DurationMs) + (IsEnded ? TimeSpan.Zero : _time.GetUtcNow().UtcDateTime - _receivedUtc);

    /// <summary>When the row ended (it leaves the tab 10 s later); null while open.</summary>
    internal DateTime? EndedUtc { get; private set; }

    internal void Update(SessionRow row)
    {
        _receivedUtc = _time.GetUtcNow().UtcDateTime;
        Source = row;
        if (IsEnded)
        {
            EndedUtc ??= _receivedUtc;
        }
    }

    internal void MarkEndedIfSo()
    {
        if (IsEnded)
        {
            EndedUtc ??= _time.GetUtcNow().UtcDateTime;
        }
    }

    internal void Tick() => OnPropertyChanged(nameof(Duration));
}

/// <summary>
/// The Sessions tab: the authentications in progress, live (docs/spec/radius-server.md "Session states"). The rows come
/// from <c>listSessions</c> when the tab opens and from the <c>sessions</c> events (batched by the server every 500 ms):
/// each batch is one pass with an index by id (at most 10,000 rows), new rows are added in one step on top, ended rows
/// leave 10 s after they ended in one step. <see cref="Tick"/> (every second while the page is shown) moves the durations.
/// </summary>
public sealed partial class SessionsViewModel : ObservableObject
{
    private readonly ICorePluginClientContext _ctx;
    private readonly TimeProvider _time;
    private readonly Dictionary<Guid, SessionRowViewModel> _byId = [];
    private int _open;
    private bool _loadedOnce;

    public SessionsViewModel(ICorePluginClientContext ctx, TimeProvider? time = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Newest first.</summary>
    public RangeObservableCollection<SessionRowViewModel> Rows { get; } = [];

    /// <summary>The tab was read once: events apply from now on.</summary>
    public bool IsLoaded => _loadedOnce;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>"3 sessions in progress".</summary>
    public string CountText => RadiusTexts.Count(_open, "session in progress", "sessions in progress");

    /// <summary>One line without rows.</summary>
    public string? EmptyText => Rows.Count > 0 || !_loadedOnce ? null : "No authentications in progress.";

    /// <summary>Reads the open sessions (the overlay the first time).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        IsLoading = !_loadedOnce;
        try
        {
            var rows = RadiusJson.Deserialize<SessionList>(await _ctx.InvokeAsync(RadiusMethods.ListSessions, null, ct).ConfigureAwait(true)).Sessions;
            _byId.Clear();
            var models = rows.OrderByDescending(r => r.StartedUtc).Select(r => new SessionRowViewModel(r, _time)).ToList();
            foreach (var model in models)
            {
                model.MarkEndedIfSo();
                _byId[model.Id] = model;
            }

            Rows.ReplaceAll(models);
            _open = models.Count(m => !m.IsEnded);
            _loadedOnce = true;
            Error = null;
            Notify();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Shown directly below the table.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Error = "Cannot read the sessions: " + ServerErrors.Message(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>One batch of the <c>sessions</c> event: rows updated in place, new ones on top in one step.</summary>
    public void Apply(IReadOnlyList<SessionRow> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (!_loadedOnce || batch.Count == 0)
        {
            return; // read with the rest when the tab opens
        }

        List<SessionRowViewModel>? added = null;
        HashSet<Guid>? removed = null;
        foreach (var row in batch)
        {
            if (row.Removed)
            {
                if (_byId.Remove(row.Id, out var gone))
                {
                    _open -= gone.IsEnded ? 0 : 1;
                    (removed ??= []).Add(row.Id);
                }

                continue;
            }

            if (_byId.TryGetValue(row.Id, out var model))
            {
                var wasOpen = !model.IsEnded;
                model.Update(row);
                _open += (model.IsEnded ? 0 : 1) - (wasOpen ? 1 : 0);
            }
            else
            {
                model = new SessionRowViewModel(row, _time);
                model.MarkEndedIfSo();
                _byId[row.Id] = model;
                _open += model.IsEnded ? 0 : 1;
                (added ??= []).Add(model);
            }
        }

        if (removed is not null)
        {
            Rows.RemoveAll(r => removed.Contains(r.Id)); // the server took them off: one step
        }

        if (added is not null)
        {
            Rows.InsertRange(0, added.OrderByDescending(m => m.StartedSortKey));
        }

        Notify();
    }

    /// <summary>Every second: ended rows older than 10 s leave (one step), open rows show their new duration.</summary>
    public void Tick()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var removed = Rows.RemoveAll(r => r.EndedUtc is { } ended && now - ended >= RadiusPageLimits.EndedSessionShown);
        if (removed > 0)
        {
            _byId.Clear();
            foreach (var row in Rows)
            {
                _byId[row.Id] = row;
            }

            Notify();
        }

        foreach (var row in Rows)
        {
            if (!row.IsEnded)
            {
                row.Tick();
            }
        }
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(IsLoaded));
    }
}
