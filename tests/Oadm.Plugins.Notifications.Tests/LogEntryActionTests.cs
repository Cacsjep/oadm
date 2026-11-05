using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Notifications.Tests;

public sealed class LogEntryActionTests
{
    [Fact]
    public async Task The_message_goes_to_the_server_log_with_the_event_values()
    {
        var log = new RecordingLogger();
        var context = new TestContext(new Dictionary<string, string> { ["objectId"] = "7" }, new TestDevice()) { Logger = log };

        await new LogEntryAction().ExecuteAsync(context, Run.Invocation(new JsonObject { ["message"] = "{device} {objectId}" }), CancellationToken.None);

        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal("Event rule Gate rule: 10.0.0.48 (AXIS P3265-V) 7", line.Text);
        Assert.Empty(context.Lines); // the event log has the run anyway
    }

    [Fact]
    public async Task A_value_stays_on_one_line_and_a_long_text_is_cut()
    {
        var log = new RecordingLogger();
        var context = new TestContext(new Dictionary<string, string> { ["text"] = "a\r\nb\nc" + new string('x', 5000) }) { Logger = log };

        await new LogEntryAction().ExecuteAsync(context, Run.Invocation(new JsonObject { ["message"] = "{text}" }), CancellationToken.None);

        var text = Assert.Single(log.Lines).Text;
        Assert.StartsWith("Event rule Gate rule: a b c", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', text);
        Assert.Equal("Event rule Gate rule: ".Length + 2000, text.Length);
    }

    [Theory]
    [InlineData(ActionEdge.TurnedOn, false, true)]
    [InlineData(ActionEdge.Fired, false, true)]
    [InlineData(ActionEdge.TurnedOff, false, false)]
    [InlineData(ActionEdge.TurnedOff, true, true)]
    public void It_writes_on_the_off_edge_only_when_asked(ActionEdge edge, bool onOff, bool runs)
    {
        using var settings = JsonDocument.Parse(new JsonObject { ["onOff"] = onOff }.ToJsonString());
        Assert.Equal(runs, new LogEntryAction().RunsOn(edge, settings.RootElement));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add((logLevel, formatter(state, exception)));
    }
}
