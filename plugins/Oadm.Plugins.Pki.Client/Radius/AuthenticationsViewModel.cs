using System.ComponentModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.Radius.Client;

/// <summary>One row of the Sessions table: an authentication in progress or one of the log.</summary>
public interface IAuthenticationRow
{
    string Time { get; }

    string Switch { get; }

    string Port { get; }

    string Mac { get; }

    string Identity { get; }

    string Device { get; }

    /// <summary>The device is a guess from the MAC (italics).</summary>
    bool IsGuess { get; }

    string IpAddress { get; }

    string VlanText { get; }

    /// <summary>The VLAN number ("30"); empty without one.</summary>
    string Vlan { get; }

    string Duration { get; }

    string Status { get; }

    string? StatusDetail { get; }

    bool IsOk { get; }

    bool IsWarning { get; }

    bool IsError { get; }

    /// <summary>In progress (the accent chip).</summary>
    bool IsRunning { get; }
}

/// <summary>
/// The one Sessions table of the 802.1X page (docs/spec/radius-server.md "What the user sees", decision 2026-10-25): the
/// authentications in progress on top (<see cref="SessionsViewModel"/>; ended ones leave, the log holds them), then the
/// log's page (<see cref="LogViewModel"/>, newest first). The log's search applies to both; the choice of a log row is the
/// log's (View certificate). Every change of either list is one reset of <see cref="Rows"/>.
/// </summary>
public sealed partial class AuthenticationsViewModel : ObservableObject
{
    private static readonly HashSet<string> Forwarded =
        [nameof(IsLoading), nameof(IsReloading), nameof(Error), nameof(HasError), nameof(EmptyText)];

    private readonly SessionsViewModel _sessions;
    private readonly LogViewModel _log;
    private bool _rebuilding;
    private int _open;

    public AuthenticationsViewModel(SessionsViewModel sessions, LogViewModel log)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        // The sessions' changes end with CountText (a batch, a read, ended rows leaving): one rebuild each, not one per step.
        log.Rows.CollectionChanged += (_, _) => Rebuild();
        sessions.PropertyChanged += OnPartChanged;
        log.PropertyChanged += OnPartChanged;
    }

    /// <summary>In progress first, then the log's page.</summary>
    public RangeObservableCollection<IAuthenticationRow> Rows { get; } = [];

    /// <summary>The chosen row; a log row is the log's choice too.</summary>
    [ObservableProperty]
    public partial IAuthenticationRow? Selected { get; set; }

    public bool IsLoading => _sessions.IsLoading || _log.IsLoading;

    public bool IsReloading => _sessions.IsReloading || _log.IsReloading;

    public string? Error => _sessions.Error ?? _log.Error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>"3 sessions in progress".</summary>
    public string CountText => RadiusTexts.Count(_open, "session in progress", "sessions in progress");

    /// <summary>One line without rows: the log's (what to do first, or nothing matches).</summary>
    public string? EmptyText => Rows.Count > 0 ? null : _log.EmptyText;

    partial void OnSelectedChanged(IAuthenticationRow? value)
    {
        if (!_rebuilding)
        {
            _log.Selected = value as LogRowViewModel;
        }
    }

    private void OnPartChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogViewModel.Selected))
        {
            if (!_rebuilding)
            {
                Selected = _log.Selected;
            }
        }
        else if (e.PropertyName is nameof(SessionsViewModel.CountText) or nameof(LogViewModel.Search))
        {
            Rebuild(); // a session ended (the log holds it), or the search changed for the rows in progress
        }

        if (e.PropertyName is { } name && Forwarded.Contains(name))
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>The rows again: one reset, the choice kept while its row is still there.</summary>
    public void Rebuild()
    {
        var search = _log.Search?.Trim() ?? string.Empty;
        var open = _sessions.Rows.Where(r => !r.IsEnded).ToList();
        _open = open.Count;
        var selected = Selected;
        _rebuilding = true;
        try
        {
            Rows.ReplaceAll([.. open.Where(r => r.Matches(search)).Cast<IAuthenticationRow>().Concat(_log.Rows)]);
            Selected = selected is not null && Rows.Contains(selected) ? selected : null;
        }
        finally
        {
            _rebuilding = false;
        }

        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(CountText));
    }
}
