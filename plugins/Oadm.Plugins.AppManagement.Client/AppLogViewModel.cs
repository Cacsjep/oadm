using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Plugins.Acap;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.AppManagement.Client;

/// <summary>
/// One log line in the grid, like the server Logs page: time, level (the status icon only: errors red, warnings, info;
/// the level as its tooltip; other levels no icon), process and message; a line that does not parse is its message only.
/// </summary>
public sealed class AppLogRow(AppLogLine line)
{
    public AppLogLine Line { get; } = line;

    public string TimeText => Line.Time?.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>"ERR", "WARNING", "INFO" as the device writes it (upper case).</summary>
    public string Level => Line.Level?.Trim().ToUpperInvariant() ?? string.Empty;

    public bool HasLevel => Level.Length > 0;

    public bool IsError => Level is "ERR" or "ERROR" or "CRIT" or "ALERT" or "EMERG";

    public bool IsWarning => Level is "WARNING" or "WARN";

    public bool IsInfo => Level is "INFO" or "NOTICE";

    /// <summary>The Level column shows an icon (error, warning, info); DEBUG and unknown levels none.</summary>
    public bool HasIcon => IsError || IsWarning || IsInfo;

    public string Process => Line.Process ?? string.Empty;

    public string Message => Line.Message;
}

/// <summary>
/// The Show log window (docs/spec/app-management.md "Log and settings"): one application's log lines on one device, read
/// through the server part (read only), newest last; Refresh, Copy and a search over the lines.
/// </summary>
public sealed partial class AppLogViewModel : ObservableObject, IDisposable
{
    public const string Truncated = "Only the newest lines are shown.";

    private readonly ICorePluginClientContext _ctx;
    private readonly Guid _deviceId;
    private readonly string _application;
    private IReadOnlyList<AppLogLine> _lines = [];
    private CancellationTokenSource? _loading;

    public AppLogViewModel(ICorePluginClientContext ctx, Guid deviceId, string application, string applicationName, string deviceName)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _deviceId = deviceId;
        _application = application;
        Title = $"Log of {applicationName} on {deviceName}";
    }

    public string Title { get; }

    /// <summary>Copies text to the clipboard (set by the window).</summary>
    public Func<string, Task>? CopyText { get; set; }

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The shown lines (after the search) as the device wrote them, newest last: what Copy copies.</summary>
    [ObservableProperty]
    public partial string Text { get; private set; } = string.Empty;

    /// <summary>The shown lines in the grid (virtualized), newest last.</summary>
    public RangeObservableCollection<AppLogRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial string EmptyText { get; private set; } = string.Empty;

    public bool ShowEmpty => !IsLoading && !HasError && EmptyText.Length > 0;

    [ObservableProperty]
    public partial bool IsTruncated { get; private set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    partial void OnSearchTextChanged(string value) => Show();

    /// <summary>The lines as read (tests).</summary>
    public IReadOnlyList<AppLogLine> Lines => _lines;

    /// <summary>Reads the log (again).</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        var loading = _loading = new CancellationTokenSource();
        IsLoading = true;
        try
        {
            var json = await _ctx.InvokeAsync(AppManagementMethods.GetLog,
                AppManagementJson.Serialize(new LogRequest { DeviceId = _deviceId, Application = _application }), loading.Token).ConfigureAwait(true);
            var reply = AppManagementJson.Deserialize<LogReply>(json);
            _lines = reply.Lines ?? [];
            IsTruncated = reply.Truncated;
            ErrorText = reply.Error;
            Show();
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // Shown in the window.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ErrorText = ServerErrors.Message(ex);
        }
        finally
        {
            if (ReferenceEquals(loading, _loading))
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Copies the shown lines.</summary>
    [RelayCommand]
    private async Task CopyAsync()
    {
        if (CopyText is { } copy && Text.Length > 0)
        {
            await copy(Text).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void Close()
    {
        _loading?.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
    }

    private void Show()
    {
        var search = SearchText.Trim();
        var shown = search.Length == 0 ? _lines : [.. _lines.Where(l => l.Text.Contains(search, StringComparison.OrdinalIgnoreCase))];
        Text = string.Join('\n', shown.Select(l => l.Text));
        Rows.ReplaceAll(shown.Select(l => new AppLogRow(l)));
        EmptyText = _lines.Count == 0 ? AppLogReader.NoLines
            : shown.Count == 0 ? "No line matches the search."
            : string.Empty;
    }
}
