using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Grpc.Core;

using Oadm.Client.Api;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;

namespace Oadm.Client.Logging;

/// <summary>One audit entry of the Audit tab.</summary>
public sealed class AuditRowViewModel
{
    public AuditRowViewModel(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Id = entry.Id;
        TimeText = entry.Time is null ? "" : entry.Time.ToDateTime().ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
        UserName = entry.UserName;
        ClientAddress = entry.ClientAddress;
        Action = entry.Action;
        Target = entry.Target;
        Detail = entry.Detail;
        IsProblem = entry.Action.Contains("failed", StringComparison.OrdinalIgnoreCase) || entry.Action.Contains("locked", StringComparison.OrdinalIgnoreCase);
        _search = string.Join('\n', TimeText, UserName, ClientAddress, Action, Target, Detail);
    }

    private readonly string _search;

    public long Id { get; }
    public string TimeText { get; }
    public string UserName { get; }
    public string ClientAddress { get; }
    public string Action { get; }
    public string Target { get; }
    public string Detail { get; }

    /// <summary>Failed or locked logins: shown with the warning chip.</summary>
    public bool IsProblem { get; }

    public bool Matches(string text) => _search.Contains(text, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Audit tab of the Logs page (Admin only): the newest <see cref="PageSize"/> entries of the server's audit log, newest
/// first, in a virtualized grid; the search filters them in one pass (one Reset).
/// </summary>
public sealed partial class AuditLogViewModel(IOadmApi api) : ObservableObject
{
    public const int PageSize = 10_000;

    private List<AuditRowViewModel> _all = [];

    public RangeObservableCollection<AuditRowViewModel> Entries { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>Total entries on the server.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    public partial int TotalCount { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    public string StatusLine
    {
        get
        {
            if (Error is not null)
            {
                return Error;
            }

            string count = _all.Count == TotalCount
                ? Plural(TotalCount, "entry", "entries")
                : string.Create(CultureInfo.CurrentCulture, $"Newest {_all.Count:N0} of {TotalCount:N0} entries");
            return Entries.Count == _all.Count ? count : string.Create(CultureInfo.CurrentCulture, $"{Entries.Count:N0} match · {count}");
        }
    }

    partial void OnSearchTextChanged(string value) => Filter();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            AuditList list = await api.ListAuditAsync(PageSize, CancellationToken.None).ConfigureAwait(true);
            _all = list.Entries.Select(e => new AuditRowViewModel(e)).ToList();
            Error = null;
            TotalCount = list.TotalCount;
            Filter();
        }
        catch (RpcException ex)
        {
            Error = "The audit log cannot be read: " + ex.Status.Detail;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Clears the rows (logout).</summary>
    public void Clear()
    {
        _all = [];
        TotalCount = 0;
        Entries.Clear();
    }

    private void Filter()
    {
        string text = SearchText.Trim();
        Entries.ReplaceAll(text.Length == 0 ? _all : _all.Where(e => e.Matches(text)).ToList());
        OnPropertyChanged(nameof(StatusLine));
    }

    private static string Plural(int n, string one, string many) =>
        n == 1 ? "1 " + one : string.Create(CultureInfo.CurrentCulture, $"{n:N0} {many}");
}
