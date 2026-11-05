using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using MQTTnet.Formatter;
using MQTTnet.Protocol;
using MQTTnet.Server;

using Oadm.Plugins.Mqtt;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Mqtt.Tests;

/// <summary>A real session against MQTTnet's in-process broker.</summary>
public sealed class MqttSessionTests
{
    private static string Text(MQTTnet.MqttApplicationMessage message) => Encoding.UTF8.GetString(message.Payload);

    private static ActionInvocation Run(object values, ActionEdge edge) =>
        new(Guid.Empty, "Rule", edge, JsonSerializer.SerializeToElement(values), null, new ActionTrigger(string.Empty, "Event", null, null, null, null, DateTimeOffset.UnixEpoch, new Dictionary<string, string>()));

    [Theory]
    [InlineData(0, "5.0")]
    [InlineData(1, "5.0")]
    [InlineData(2, "5.0")]
    [InlineData(0, "3.1.1")]
    [InlineData(1, "3.1.1")]
    [InlineData(2, "3.1.1")]
    public async Task EveryQosArrives(int qos, string version)
    {
        await using var broker = await TestBroker.StartAsync();
        await using var session = new MqttSession(Sessions.Request(broker.Port, new { protocolVersion = version }));
        await session.ConnectAsync(Sessions.Patient);
        await session.PublishAsync(new MqttMessage("site/door", "open", qos, false), Sessions.Patient);

        await Eventually.TrueAsync(() => !broker.Messages.IsEmpty, "the broker has the message");
        Assert.True(broker.Messages.TryPeek(out var message));
        Assert.Equal("site/door", message.Topic);
        Assert.Equal("open", Text(message));
        Assert.Equal((MqttQualityOfServiceLevel)qos, message.QualityOfServiceLevel);
        Assert.False(message.Retain);
    }

    [Fact]
    public async Task TheProtocolVersionIsTheChosenOne()
    {
        var versions = new ConcurrentQueue<MqttProtocolVersion>();
        await using var broker = await TestBroker.StartAsync(server => server.ValidatingConnectionAsync += e =>
        {
            versions.Enqueue(e.ProtocolVersion);
            return Task.CompletedTask;
        });
        await using (var old = new MqttSession(Sessions.Request(broker.Port, new { protocolVersion = "3.1.1" })))
        {
            await old.ConnectAsync(Sessions.Patient);
        }

        await using var current = new MqttSession(Sessions.Request(broker.Port, new { protocolVersion = "5.0" }));
        await current.ConnectAsync(Sessions.Patient);
        Assert.Equal([MqttProtocolVersion.V311, MqttProtocolVersion.V500], versions);
    }

    [Fact]
    public async Task ARetainedMessageStaysOnTheBroker()
    {
        await using var broker = await TestBroker.StartAsync();
        await using var session = new MqttSession(Sessions.Request(broker.Port));
        await session.ConnectAsync(Sessions.Patient);
        await session.PublishAsync(new MqttMessage("state", "on", 1, true), Sessions.Patient);

        await Eventually.TrueAsync(async () => await broker.Server.GetRetainedMessageAsync("state") is not null, "the broker keeps the message");
        var retained = await broker.Server.GetRetainedMessageAsync("state");
        Assert.True(retained.Retain);
        Assert.Equal("on", Text(retained));
    }

    [Fact]
    public async Task TheOffEdgeReachesTheBroker()
    {
        await using var broker = await TestBroker.StartAsync();
        await using var session = new MqttSession(Sessions.Request(broker.Port));
        await session.ConnectAsync(Sessions.Patient);
        var context = new FakeActionContext();
        context.Sessions["recipient"] = session;
        var settings = new { topic = "state", payload = "on", offPayload = "off", qos = "1" };

        var action = new PublishMqttAction();
        await action.ExecuteAsync(context, Run(settings, ActionEdge.TurnedOn), Sessions.Patient);
        await action.ExecuteAsync(context, Run(settings, ActionEdge.TurnedOff), Sessions.Patient);

        await Eventually.TrueAsync(() => broker.Messages.Count == 2, "the broker has both messages");
        Assert.Equal(["on", "off"], broker.Messages.Select(Text));
    }

    [Fact]
    public async Task ManyActionsShareOneSession()
    {
        await using var broker = await TestBroker.StartAsync();
        await using var session = new MqttSession(Sessions.Request(broker.Port));
        await session.ConnectAsync(Sessions.Patient);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(n => session.PublishAsync(new MqttMessage("t", n.ToString(System.Globalization.CultureInfo.InvariantCulture), 1 + (n % 2), false), Sessions.Patient)));
        await Eventually.TrueAsync(() => broker.Messages.Count == 50, "the broker has every message");
        Assert.Single(await broker.Server.GetClientsAsync());
    }

    [Fact]
    public async Task TheLoginAndClientIdReachTheBroker()
    {
        var logins = new ConcurrentQueue<(string? User, string Password, string ClientId)>();
        await using var broker = await TestBroker.StartAsync(server => server.ValidatingConnectionAsync += e =>
        {
            logins.Enqueue((e.UserName, e.Password, e.ClientId));
            return Task.CompletedTask;
        });
        await using var session = new MqttSession(Sessions.Request(broker.Port, new { user = "oadm", password = "secret", clientId = "line-3" }));
        await session.ConnectAsync(Sessions.Patient);
        Assert.Equal(("oadm", "secret", "line-3"), Assert.Single(logins));
    }

    [Fact]
    public async Task NoClientIdMeansTheDefaultOne()
    {
        var ids = new ConcurrentQueue<string>();
        await using var broker = await TestBroker.StartAsync(server => server.ValidatingConnectionAsync += e =>
        {
            ids.Enqueue(e.ClientId);
            return Task.CompletedTask;
        });
        var recipient = Guid.NewGuid();
        await using var session = new MqttSession(Sessions.Request(broker.Port, new { clientId = " " }) with { RecipientId = recipient });
        await session.ConnectAsync(Sessions.Patient);
        Assert.Equal(MqttRecipient.DefaultClientId(recipient), Assert.Single(ids));
    }

    [Theory]
    [InlineData("5.0")]
    [InlineData("3.1.1")]
    public async Task ARefusedLoginSaysSo(string version)
    {
        await using var broker = await TestBroker.StartAsync(server => server.ValidatingConnectionAsync += e =>
        {
            e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            return Task.CompletedTask;
        });
        await using var session = new MqttSession(Sessions.Request(broker.Port, new { protocolVersion = version, user = "oadm", password = "wrong-secret" }));
        var dropped = 0;
        session.Dropped += (_, _) => Interlocked.Increment(ref dropped);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(Sessions.Patient));
        Assert.Equal("The broker refused the login. Check the user name and password.", refused.Message);
        Assert.DoesNotContain("wrong-secret", refused.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public async Task AnUnreachableBrokerSaysSo()
    {
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); // bound, so no other test gets the port; not listening
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)reserved.LocalEndPoint!).Port;
        await using var session = new MqttSession(Sessions.Request(port));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(Sessions.Patient));
        Assert.StartsWith($"127.0.0.1:{port} cannot be reached", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACancelledConnectIsACancellation()
    {
        await using var broker = await TestBroker.StartAsync();
        await using var session = new MqttSession(Sessions.Request(broker.Port));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ConnectAsync(new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task TlsAgainstAPlainBrokerSaysToCheckTls()
    {
        await using var broker = await TestBroker.StartAsync();
        await using var session = new MqttSession(Sessions.Request(broker.Port, new { tls = true, allowUntrusted = true }));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ConnectAsync(Sessions.Patient));
        Assert.Contains("TLS", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStoppedBrokerDropsTheSessionOnce()
    {
        await using var broker = await TestBroker.StartAsync();
        await using var session = new MqttSession(Sessions.Request(broker.Port));
        var reasons = new ConcurrentQueue<string>();
        session.Dropped += (_, e) => reasons.Enqueue(e.Reason);
        await session.ConnectAsync(Sessions.Patient);

        await broker.Server.StopAsync(new MqttServerStopOptions());

        await Eventually.TrueAsync(() => !reasons.IsEmpty, "the session reports the drop");
        Assert.True(reasons.TryPeek(out var reason));
        Assert.Contains(reason, new[] { "The broker is shutting down", "The connection ended" });
        var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => session.PublishAsync(new MqttMessage("t", "p", 1, false), Sessions.Patient));
        Assert.Equal(RecipientSessionTexts.NotConnected(reason), failed.Message);
        Assert.Single(reasons);
    }

    [Fact]
    public async Task ASecondConnectionWithTheClientIdSaysSo()
    {
        await using var broker = await TestBroker.StartAsync();
        await using var first = new MqttSession(Sessions.Request(broker.Port, new { clientId = "same" }));
        var reasons = new ConcurrentQueue<string>();
        first.Dropped += (_, e) => reasons.Enqueue(e.Reason);
        await first.ConnectAsync(Sessions.Patient);

        await using var second = new MqttSession(Sessions.Request(broker.Port, new { clientId = "same" }));
        await second.ConnectAsync(Sessions.Patient);

        await Eventually.TrueAsync(() => !reasons.IsEmpty, "the first session reports the drop");
        Assert.True(reasons.TryPeek(out var reason));
        Assert.Equal("Another connection uses this client ID", reason);
    }

    [Fact]
    public async Task ClosingTheSessionIsNoDrop()
    {
        await using var broker = await TestBroker.StartAsync();
        var left = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        broker.Server.ClientDisconnectedAsync += _ =>
        {
            left.TrySetResult();
            return Task.CompletedTask;
        };
        var session = new MqttSession(Sessions.Request(broker.Port));
        var dropped = 0;
        session.Dropped += (_, _) => Interlocked.Increment(ref dropped);
        await session.ConnectAsync(Sessions.Patient);

        await session.DisposeAsync();

        await left.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(0, dropped);
        await session.DisposeAsync(); // the pool may close a session that dropped
    }

    [Fact]
    public async Task ABrokersRefusalFailsTheAction()
    {
        await using var broker = await TestBroker.StartAsync(server => server.InterceptingPublishAsync += e =>
        {
            e.ProcessPublish = false;
            e.Response.ReasonCode = MqttPubAckReasonCode.NotAuthorized;
            return Task.CompletedTask;
        });
        await using var session = new MqttSession(Sessions.Request(broker.Port));
        await session.ConnectAsync(Sessions.Patient);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => session.PublishAsync(new MqttMessage("t", "p", 1, false), Sessions.Patient));
        Assert.Equal("The broker does not allow this topic.", refused.Message);
    }

    [Fact]
    public async Task NoSubscriberIsNoFailure()
    {
        await using var broker = await TestBroker.StartAsync(server => server.InterceptingPublishAsync += e =>
        {
            e.ProcessPublish = false;
            e.Response.ReasonCode = MqttPubAckReasonCode.NoMatchingSubscribers;
            return Task.CompletedTask;
        });
        await using var session = new MqttSession(Sessions.Request(broker.Port));
        await session.ConnectAsync(Sessions.Patient);
        await session.PublishAsync(new MqttMessage("t", "p", 1, false), Sessions.Patient);
    }

    [Fact]
    public async Task ABrokerThatNeverAcknowledgesFailsTheAction()
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var broker = await TestBroker.StartAsync(server => server.InterceptingPublishAsync += async _ =>
        {
            held.TrySetResult();
            await release.Task;
        });
        try
        {
            var time = new FakeTimeProvider();
            await using var session = new MqttSession(Sessions.Request(broker.Port), time);
            await session.ConnectAsync(Sessions.Patient);

            var publish = session.PublishAsync(new MqttMessage("t", "p", 1, false), Sessions.Patient);
            await held.Task.WaitAsync(TimeSpan.FromSeconds(30));
            time.Advance(MqttSession.AnswerTimeout);

            var failed = await Assert.ThrowsAsync<InvalidOperationException>(() => publish.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.Equal($"127.0.0.1:{broker.Port} did not answer. The message may have been sent.", failed.Message);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task TheFactoryMakesASessionThatIsNotConnected()
    {
        await using var broker = await TestBroker.StartAsync();
        await using var session = MqttRecipient.Type.Sessions!.Create(Sessions.Request(broker.Port));
        Assert.Empty(await broker.Server.GetClientsAsync());
        await session.ConnectAsync(Sessions.Patient);
        await Eventually.TrueAsync(async () => (await broker.Server.GetClientsAsync()).Count == 1, "the broker has the client");
    }
}
