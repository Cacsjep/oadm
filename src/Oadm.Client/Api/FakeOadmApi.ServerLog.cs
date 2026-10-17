using System.Globalization;

using Oadm.Contracts.Logging;
using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake server log of the Logs page: 650 entries over the last hours, newest first, with warnings and errors of event
/// rule actions, device polls and plugins; paged and filtered like the server.
/// </summary>
public sealed partial class FakeOadmApi
{
    /// <summary>How many entries the fake server log holds.</summary>
    public const int FakeServerLogCount = 650;

    private static readonly Lazy<List<LogFileEntry>> FakeServerLog = new(() =>
    {
        var start = new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.FromHours(2));
        (string Level, string Source, string Message, string Exception)[] samples =
        [
            ("Information", "Oadm.Server.Devices.DevicePollingHostedService", "Poll round finished: 48 devices, 47 Ok, 1 Unreachable", ""),
            ("Information", "Oadm.Core.Tasks.TaskEngine", "Task Restart device on 10.0.0.48 done", ""),
            ("Warning", "Oadm.Core.Events.ActionRunner", "Event rule Person in the yard: notify: action Send HTTP request failed: hooks.example.com cannot be reached: Connection refused", ""),
            ("Information", "Oadm.Core.Events.RuleEngine", "Event rule Person in the yard: warning lamp turned on for 10.0.0.48", ""),
            ("Warning", "Oadm.Plugins.DeviceTriggers.TriggerConnections", "10.0.0.202: the event connection was lost; trying again in 30 s", ""),
            ("Error", "Oadm.Core.Events.ActionRunner", "Event rule Gate open: email: action Send email failed unexpectedly",
                "System.IO.IOException: Unable to read data from the transport connection.\n   at MailKit.Net.Smtp.SmtpClient.ConnectAsync(...)"),
            ("Information", "Oadm.Server.Auth.AuthGrpcService", "admin logged in from 10.0.0.15", ""),
            ("Information", "Oadm.Core.Plugins.CorePluginHost", "Core plugin oadm.ntp-server started", ""),
        ];
        var entries = new List<LogFileEntry>(FakeServerLogCount);
        for (var i = 0; i < FakeServerLogCount; i++)
        {
            var (level, source, message, exception) = samples[i % samples.Length];
            entries.Add(new LogFileEntry(start.AddSeconds(i * 37), level, source, message, exception));
        }

        entries.Reverse(); // newest first
        return entries;
    });

    public Task<ServerLogPage> ListServerLogAsync(int offset, int limit, string minLevel, string search, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
        }

        var minRank = string.IsNullOrEmpty(minLevel) ? 0 : LogFileReader.Rank(minLevel);
        var matching = FakeServerLog.Value.Where(e => e.Rank >= minRank
            && (string.IsNullOrWhiteSpace(search)
                || e.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Source.Contains(search, StringComparison.OrdinalIgnoreCase)
                || e.Exception.Contains(search, StringComparison.OrdinalIgnoreCase)));
        var page = matching.Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, LogFileReader.MaxPageSize) + 1).ToList();
        var reply = new ServerLogPage { HasMore = page.Count > limit };
        reply.Entries.AddRange(page.Take(limit).Select(e => new ServerLogEntry
        {
            Time = e.Time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            Level = e.Level,
            Source = e.Source,
            Message = e.Message,
            Exception = e.Exception,
        }));
        return Task.FromResult(reply);
    }
}
