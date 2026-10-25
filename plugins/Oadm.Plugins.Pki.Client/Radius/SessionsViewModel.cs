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
public sealed partial class SessionRowViewModel : ObservableObject, IAuthenticationRow
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
        nameof(State), nameof(Status), nameof(Vlan), nameof(VlanText), nameof(IsOk), nameof(IsError), nameof(IsRunning), nameof(IsEnded), nameof(Duration))]
    public partial SessionRow Source { get; set; }

    public Guid Id => Source.Id;

    public string Started => RadiusTexts.Time(Source.StartedUtc);

    public DateTime StartedSortKey => Source.StartedUtc;

    /// <summary>The Time column of the one table: when it started.</summary>
    public string Time => Started;

    /// <summary>The VLAN number ("30") once the session has one; empty before.</summary>
    public string Vlan => Source.Vlan?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>"VLAN 30 by Building B" once the session has one; empty before.</summary>
    public string VlanText => Source.Vlan is { } vlan ? Oadm.Plugins.Pki.Policies.PolicyTexts.VlanBy(vlan, Source.VlanPolicy) : string.Empty;

    /// <summary>The Status column: the state.</summary>
    public string Status => State;

    /// <summary>No second line: the state says it all.</summary>
    public string? StatusDetail => null;

    public bool IsWarning => false;

    public string Switch => Source.SwitchName;

    /// <summary>"GigabitEthernet1/0/5" or "17", as the switch names it (no port type: decision 2026-10-25).</summary>
    public string Port => Source.Port;

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

    /// <summary>The search text is in a column (the log's search applies to the rows in progress too).</summary>
    public bool Matches(string search) =>
        search.Length == 0
        || Time.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Switch.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Port.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Mac.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Identity.Contains(search, StringComparison.OrdinalIgnoreCase)
        || Device.Contains(search, StringComparison.OrdinalIgnoreCase)
        || IpAddress.Contains(search, StringComparison.OrdinalIgnoreCase)
        || State.Contains(search, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The Sessions tab: the authentications in progress, live (docs/spec/radius-server.md "Session states"). The rows come
/// from <c>listSessions</c> when the tab opens and from the <c>sessions</c> events (batched by the server every 500 ms):
/// each batch is one pass with an index by id (at most 10,000 rows), new rows are added in one step on top, ended rows
/// leave 10 s after they ended in one step. <see cref="Tick"/> (every second while the page is shown) moves the durations.
/// Batches that arrive while the list is read are kept and applied after it (in order, never older than the read rows); a
/// list read while the page was away is stale (<see cref="MarkStale"/>) and read again when the tab opens.
/// </summary>
public sealed partial class SessionsViewModel : ObservableObject
{
    private readonly ICorePluginClientContext _ctx;
    private readonly TimeProvider _time;
    private readonly Dictionary<Guid, SessionRowViewModel> _byId = [];
    private int _open;
    private bool _loadedOnce;
    private bool _stale;
    private int _readVersion;
    private List<IReadOnlyList<SessionRow>>? _pending; // batches that came in while the list was read

    public SessionsViewModel(ICorePluginClientContext ctx, TimeProvider? time = null)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Newest first.</summary>
    public RangeObservableCollection<SessionRowViewModel> Rows { get; } = [];

    /// <summary>The tab was read once: events apply from now on.</summary>
    public bool IsLoaded => _loadedOnce;

    /// <summary>The tab must be read when it opens: never read, or read before the page was left (no events meanwhile).</summary>
    public bool NeedsLoad => !_loadedOnce || _stale;

    /// <summary>The first read (the overlay).</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>A later read (the page came back): the progress row, the rows stay.</summary>
    [ObservableProperty]
    public partial bool IsReloading { get; set; }

    /// <summary>The page was left: no events came in since, the rows are read again when the tab opens.</summary>
    public void MarkStale() => _stale = _loadedOnce;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>"3 sessions in progress".</summary>
    public string CountText => RadiusTexts.Count(_open, "session in progress", "sessions in progress");

    /// <summary>The sessions in progress (not ended).</summary>
    public int OpenCount => _open;

    /// <summary>The row is still one of the tab's (not taken off by the server or after it ended).</summary>
    public bool Holds(SessionRowViewModel row) => row is not null && _byId.TryGetValue(row.Id, out var held) && ReferenceEquals(held, row);

    /// <summary>
    /// After a batch of the live events: the rows it added (null: none). Rows it updated changed in place; the Sessions table
    /// applies it in place too (never one reset per batch).
    /// </summary>
    public event EventHandler<IReadOnlyList<SessionRowViewModel>?>? BatchApplied;

    /// <summary>After the list was read again (the Sessions table builds its rows again).</summary>
    public event EventHandler? Reloaded;

    /// <summary>One line without rows.</summary>
    public string? EmptyText => Rows.Count > 0 || !_loadedOnce ? null : "No authentications in progress.";

    /// <summary>Reads the open sessions (the overlay the first time).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var version = ++_readVersion;
        IsLoading = !_loadedOnce;
        IsReloading = _loadedOnce;
        _pending ??= [];
        try
        {
            var rows = RadiusJson.Deserialize<SessionList>(await _ctx.InvokeAsync(RadiusMethods.ListSessions, null, ct).ConfigureAwait(true)).Sessions;
            if (version != _readVersion)
            {
                return; // a newer read answers
            }

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
            _stale = false;
            Error = null;
            var pending = _pending;
            _pending = null;
            foreach (var batch in pending ?? [])
            {
                ApplyBatch(batch, buffered: true);
            }

            Notify();
            Reloaded?.Invoke(this, EventArgs.Empty);
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
            if (version == _readVersion)
            {
                _pending = null;
                IsLoading = false;
                IsReloading = false;
            }
        }
    }

    /// <summary>
    /// One batch of the <c>sessions</c> event: rows updated in place, new ones on top in one step. While the list is read the
    /// batch waits and is applied after it.
    /// </summary>
    public void Apply(IReadOnlyList<SessionRow> batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return;
        }

        if (_pending is { } pending)
        {
            pending.Add(batch); // applied after the read
            return;
        }

        if (!_loadedOnce)
        {
            return; // read with the rest when the tab opens
        }

        var added = ApplyBatch(batch, buffered: false);
        Notify();
        BatchApplied?.Invoke(this, added);
    }

    /// <summary>
    /// Applies one batch. A <paramref name="buffered"/> one came in during the read: an update older than the read row
    /// (a shorter duration) is left out.
    /// </summary>
    private List<SessionRowViewModel>? ApplyBatch(IReadOnlyList<SessionRow> batch, bool buffered)
    {
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
                if (buffered && row.DurationMs < model.Source.DurationMs)
                {
                    continue; // the read row is newer
                }

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
            added = [.. added.OrderByDescending(m => m.StartedSortKey)];
            Rows.InsertRange(0, added);
        }

        return added;
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
