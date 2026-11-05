using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Events;
using Oadm.Sdk.Actions;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.Events;

/// <summary>The shared connections of persistent recipients (docs/spec/recipient-sessions.md), with a fake session and fake time.</summary>
public sealed class RecipientSessionPoolTests
{
    private const string TypeId = "test.persistent";
    private const string ActionId = "test.persistent.send";

    private static readonly ActionField[] Fields = [ActionField.Text("host", "Host", required: true), ActionField.Password("password", "Password")];

    private sealed class FakeSession(FakeFactory factory, RecipientSessionRequest request) : IRecipientSession
    {
        public event EventHandler<RecipientDroppedEventArgs>? Dropped;

        public RecipientSessionRequest Request { get; } = request;

        public bool Disposed { get; private set; }

        public Task ConnectAsync(CancellationToken ct) => factory.Connect(ct);

        public void Drop(string reason) => Dropped?.Invoke(this, new RecipientDroppedEventArgs(reason));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return factory.Close();
        }
    }

    private sealed class FakeFactory : IRecipientSessionFactory
    {
        public ConcurrentQueue<FakeSession> Sessions { get; } = new();

        public Func<CancellationToken, Task> Connect { get; set; } = _ => Task.CompletedTask;

        public Func<ValueTask> Close { get; set; } = () => ValueTask.CompletedTask;

        public IRecipientSession Create(RecipientSessionRequest request)
        {
            var session = new FakeSession(this, request);
            Sessions.Enqueue(session);
            return session;
        }
    }

    private sealed class Action(FakeFactory factory) : IActionPlugin
    {
        public string Id => ActionId;

        public string DisplayName => "Send over a session";

        public string? IconKey => null;

        public bool NeedsDevice => false;

        public bool SupportsTurnOff => false;

        public ConcurrentQueue<FakeSession> Used { get; } = new();

        public IReadOnlyList<RecipientType> RecipientTypes => [new(TypeId, "Persistent", null, RecipientSessionPoolTests.Fields, ActionId) { Sessions = factory, Target = "{host}" }];

        public IReadOnlyList<ActionField> Fields => [ActionField.Recipient("recipient", "Recipient", [TypeId])];

        public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule) => new Dictionary<string, string>();

        public string Describe(JsonElement settings) => string.Empty;

        public bool RunsOn(ActionEdge edge, JsonElement settings) => true;

        public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct) =>
            Used.Enqueue(await ctx.SessionAsync<FakeSession>("recipient", ct));
    }

    private sealed class Stage(EventRulesRig rig, RecipientSessionPool pool, FakeFactory factory, WatchedTime timers, Action action) : IAsyncDisposable
    {
        public Action Action { get; } = action;

        public EventRulesRig Rig { get; } = rig;

        public RecipientSessionPool Pool { get; } = pool;

        public FakeFactory Factory { get; } = factory;

        public WatchedTime Timers { get; } = timers;

        public FakeTimeProvider Time => Rig.Time;

        public async Task<Guid> RecipientAsync(string name = "Broker")
        {
            var (saved, _) = await Rig.Recipients.SaveAsync(null, name, TypeId, new JsonObject { ["host"] = "broker", ["password"] = "s3cret" }, Fields, CancellationToken.None);
            return saved.Id;
        }

        public RecipientConnectionState State(Guid id) =>
            Pool.Statuses().FirstOrDefault(s => s.Id == id)?.State ?? RecipientConnectionState.NotUsed;

        public async ValueTask DisposeAsync()
        {
            await Pool.DisposeAsync();
            await Rig.DisposeAsync();
        }
    }

    private static async Task<Stage> StageAsync()
    {
        var factory = new FakeFactory();
        var action = new Action(factory);
        var rig = await EventRulesRig.StartAsync(beforeStart: r => r.Registry.RegisterActionPlugin(action, r.ActionOrigin));
        var timers = new WatchedTime(rig.Time);
        var pool = new RecipientSessionPool(rig.Catalog, rig.Recipients, () => null, timers);
        rig.Runner.Sessions = pool;
        return new Stage(rig, pool, factory, timers, action);
    }

    [Fact]
    public async Task ManyActionsShareOneSessionWithDecryptedSettings()
    {
        await using var stage = await StageAsync();
        var id = await stage.RecipientAsync();

        var sessions = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => stage.Pool.GetAsync(id, CancellationToken.None)));

        Assert.Single(sessions.Distinct());
        var session = Assert.Single(stage.Factory.Sessions);
        Assert.Equal("s3cret", session.Request.Get("password"));
        Assert.Equal(RecipientConnectionState.Connected, stage.State(id));
    }

    [Fact]
    public async Task AnActionGetsTheSessionOfItsRecipient()
    {
        await using var stage = await StageAsync();
        var id = await stage.RecipientAsync();

        var (ok, message) = await stage.Rig.Runner.TestAsync(ActionId, new JsonObject { ["recipient"] = id.ToString() }, "Rule", null, null, null, null, CancellationToken.None);

        Assert.True(ok, message);
        Assert.Same(Assert.Single(stage.Factory.Sessions), Assert.Single(stage.Action.Used));
        Assert.Equal(id, stage.Action.Used.Single().Request.RecipientId);
    }

    [Fact]
    public async Task TheTestOfAPersistentRecipientOnlyConnectsAndHasNoId()
    {
        await using var stage = await StageAsync();

        var (ok, message) = await stage.Rig.Runner.TestRecipientAsync(TypeId, null, new JsonObject { ["host"] = "broker" }, CancellationToken.None);

        Assert.True(ok, message);
        Assert.Equal("Connected", message);
        Assert.Empty(stage.Action.Used); // the action never ran: Test writes nothing to a real machine
        Assert.Null(Assert.Single(stage.Factory.Sessions).Request.RecipientId);
    }

    [Fact]
    public async Task ARecipientNoActionUsedHoldsNoConnection()
    {
        await using var stage = await StageAsync();
        var id = await stage.RecipientAsync();

        Assert.Empty(stage.Factory.Sessions);
        Assert.Equal(RecipientConnectionState.NotUsed, stage.State(id));
    }

    [Fact]
    public async Task AFailedConnectionFailsAtOnceAndTheWaitGrowsAndStartsAgain()
    {
        await using var stage = await StageAsync();
        var id = await stage.RecipientAsync();
        var attempts = 0;
        stage.Factory.Connect = _ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.FromException(new InvalidOperationException("Broker refused"));
        };

        var first = await Assert.ThrowsAsync<InvalidOperationException>(() => stage.Pool.GetAsync(id, CancellationToken.None));
        Assert.Equal("Not connected: Broker refused", first.Message);
        Assert.Equal(1, Volatile.Read(ref attempts));

        // The second action does not wait for a new attempt.
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() => stage.Pool.GetAsync(id, CancellationToken.None));
        Assert.Equal("Not connected: Broker refused", second.Message);

        for (var round = 2; round <= 4; round++)
        {
            var wait = TimeSpan.FromSeconds(1 << (round - 2));
            await stage.Timers.CreatedAsync(wait);
            stage.Time.Advance(wait);
            var expected = round;
            await Eventually.TrueAsync(() => Volatile.Read(ref attempts) >= expected, "the next attempt");
        }

        // Not one second earlier than the wait: 8 s now.
        await stage.Timers.CreatedAsync(TimeSpan.FromSeconds(8));
        stage.Time.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(4, Volatile.Read(ref attempts));

        stage.Factory.Connect = _ =>
        {
            Interlocked.Increment(ref attempts);
            return Task.CompletedTask;
        };
        stage.Time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.TrueAsync(() => stage.State(id) == RecipientConnectionState.Connected, "the reconnect");

        // A dropped connection starts again at 1 s.
        Assert.True(stage.Factory.Sessions.TryPeek(out _));
        stage.Factory.Sessions.Last().Drop("Broker gone");
        await Eventually.TrueAsync(() => stage.State(id) == RecipientConnectionState.NotConnected, "the drop");
        Assert.Equal("Broker gone", Assert.Single(stage.Pool.Statuses()).Reason);
        await stage.Timers.CreatedAsync(TimeSpan.FromSeconds(1));
        stage.Time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.TrueAsync(() => stage.State(id) == RecipientConnectionState.Connected, "the reconnect after the drop");
    }

    [Fact]
    public async Task AnUnusedSessionClosesAfterFiveMinutes()
    {
        await using var stage = await StageAsync();
        var id = await stage.RecipientAsync();
        await stage.Pool.GetAsync(id, CancellationToken.None);
        var session = Assert.Single(stage.Factory.Sessions);

        await stage.Timers.CreatedAsync(TimeSpan.FromMinutes(5));
        stage.Time.Advance(TimeSpan.FromMinutes(4));
        await stage.Pool.GetAsync(id, CancellationToken.None); // used again: 5 minutes from here
        stage.Time.Advance(TimeSpan.FromMinutes(1.5));
        await stage.Timers.CreatedAsync(TimeSpan.FromMinutes(3.5));
        Assert.False(session.Disposed);

        stage.Time.Advance(TimeSpan.FromMinutes(3.5));
        await Eventually.TrueAsync(() => session.Disposed, "the idle close");
        await Eventually.TrueAsync(() => stage.State(id) == RecipientConnectionState.NotUsed, "the status");
    }

    [Fact]
    public async Task AChangedRecipientClosesItsSessionAndTheNextActionOpensTheNewOne()
    {
        await using var stage = await StageAsync();
        var id = await stage.RecipientAsync();
        await stage.Pool.GetAsync(id, CancellationToken.None);
        var session = Assert.Single(stage.Factory.Sessions);

        await stage.Rig.Recipients.SaveAsync(id, "Broker", TypeId, new JsonObject { ["host"] = "other" }, Fields, CancellationToken.None);
        await Eventually.TrueAsync(() => session.Disposed, "the close");

        await stage.Pool.GetAsync(id, CancellationToken.None);
        Assert.Equal("other", stage.Factory.Sessions.Last().Request.Get("host"));
        Assert.Equal("s3cret", stage.Factory.Sessions.Last().Request.Get("password"));
    }

    [Fact]
    public async Task ADeletedRecipientClosesItsSession()
    {
        await using var stage = await StageAsync();
        var id = await stage.RecipientAsync();
        await stage.Pool.GetAsync(id, CancellationToken.None);
        var session = Assert.Single(stage.Factory.Sessions);

        await stage.Rig.Recipients.DeleteAsync(id, CancellationToken.None);

        await Eventually.TrueAsync(() => session.Disposed, "the close");
        await Assert.ThrowsAsync<InvalidOperationException>(() => stage.Pool.GetAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task AnAttemptWithoutAnAnswerEndsAtTheTimeout()
    {
        await using var stage = await StageAsync();
        var id = await stage.RecipientAsync();
        stage.Factory.Connect = ct => Task.Delay(Timeout.Infinite, CancellationToken.None); // ignores its token
        var waiting = stage.Pool.GetAsync(id, CancellationToken.None);

        await stage.Timers.CreatedAsync(TimeSpan.FromSeconds(15));
        stage.Time.Advance(TimeSpan.FromSeconds(15));

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => waiting);
        Assert.Equal("Not connected: Timeout - no answer", refused.Message);
    }

    [Fact]
    public async Task AStuckCloseNeverHoldsUpAStop()
    {
        await using var stage = await StageAsync();
        var id = await stage.RecipientAsync();
        await stage.Pool.GetAsync(id, CancellationToken.None);
        stage.Factory.Close = () => new ValueTask(Task.Delay(Timeout.Infinite, CancellationToken.None));

        var stop = stage.Pool.DisposeAsync().AsTask();
        await stage.Timers.CreatedAsync(TimeSpan.FromSeconds(5));
        stage.Time.Advance(TimeSpan.FromSeconds(5));
        await stop.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TheTestOfARecipientConnectsOnceAndClosesAndNeverUsesThePool()
    {
        await using var stage = await StageAsync();
        var type = stage.Rig.Catalog.FindRecipientType(TypeId)!.Type;

        var (ok, message) = await stage.Pool.TestAsync(type, new JsonObject { ["host"] = "broker" }, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("Connected", message);
        Assert.True(Assert.Single(stage.Factory.Sessions).Disposed);
        Assert.Empty(stage.Pool.Statuses());

        stage.Factory.Connect = _ => Task.FromException(new InvalidOperationException("Wrong password"));
        Assert.Equal((false, "Wrong password"), await stage.Pool.TestAsync(type, new JsonObject { ["host"] = "broker" }, CancellationToken.None));
    }

    [Fact]
    public async Task TheEventFollowsTheConnections()
    {
        await using var stage = await StageAsync();
        var published = new RecordingPublisher();
        var source = new RecipientEventSource(stage.Pool, () => published);
        source.Start();
        Assert.Equal([0], published.Reports); // the first full report makes the type ready

        var id = await stage.RecipientAsync("Plant broker");
        stage.Factory.Connect = _ => Task.FromException(new InvalidOperationException("Refused"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stage.Pool.GetAsync(id, CancellationToken.None));
        await Eventually.TrueAsync(() => published.Last is not null, "the event");
        Assert.Equal("1", published.Last![RecipientEventSource.CountField]);
        Assert.Equal("Plant broker", published.Last[RecipientEventSource.RecipientField]);

        stage.Factory.Connect = _ => Task.CompletedTask;
        await stage.Timers.CreatedAsync(TimeSpan.FromSeconds(1));
        stage.Time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.TrueAsync(() => published.Reports.Count > 1 && published.Reports.Last() == 0, "the event ends");
    }

    /// <summary>The fake clock of the rig; a test waits for the timer of a wait before it advances, and proves the wait's length.</summary>
    private sealed class WatchedTime(FakeTimeProvider time) : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<TimeSpan> _created = [];

        public override DateTimeOffset GetUtcNow() => time.GetUtcNow();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                _created.Add(dueTime);
            }

            return time.CreateTimer(callback, state, dueTime, period);
        }

        public Task CreatedAsync(TimeSpan dueTime) => Eventually.TrueAsync(
            () =>
            {
                lock (_gate)
                {
                    return _created.Remove(dueTime);
                }
            },
            "a timer of " + dueTime);
    }

    private sealed class RecordingPublisher : Oadm.Sdk.Events.IEventPublisher
    {
        public IReadOnlyDictionary<string, string>? Last { get; private set; }

        public ConcurrentQueue<int> Reports { get; } = new();

        public void SetState(string typeId, Guid? deviceId, bool isOn, IReadOnlyDictionary<string, string>? data = null) => Last = data;

        public void ReplaceStates(string typeId, IReadOnlyCollection<Guid> onDevices) => Reports.Enqueue(onDevices.Count);

        public void Fire(string typeId, Guid? deviceId, IReadOnlyDictionary<string, string>? data = null)
        {
        }

        public bool IsWatched(string typeId) => true;
    }
}
