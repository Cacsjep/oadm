using System.Collections.ObjectModel;
using System.Globalization;

using Oadm.Client.Infrastructure;

using Serilog.Core;
using Serilog.Events;

namespace Oadm.Client.Logging;

public sealed record LogEntry(DateTime Time, string Level, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public bool IsError => Level is "Error" or "Fatal";
    public bool IsWarning => Level == "Warning";
}

/// <summary>Recent client log lines for the Log tab. Fed by a Serilog sink.</summary>
public sealed class LogStore(IUiDispatcher dispatcher)
{
    public const int Capacity = 500;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    public void Add(LogEntry entry)
    {
        dispatcher.Post(() =>
        {
            Entries.Insert(0, entry);
            while (Entries.Count > Capacity)
            {
                Entries.RemoveAt(Entries.Count - 1);
            }
        });
    }
}

public sealed class LogStoreSink(LogStore store) : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        string message = logEvent.RenderMessage(CultureInfo.CurrentCulture);
        if (logEvent.Exception is not null)
        {
            message += " - " + logEvent.Exception.Message;
        }

        store.Add(new LogEntry(logEvent.Timestamp.LocalDateTime, logEvent.Level.ToString(), message));
    }
}
