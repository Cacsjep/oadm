using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Auth;
using Oadm.Server.LiveView;
using Oadm.Server.Mcp;
using Oadm.Server.Tests.Support;
using Oadm.Server.Tools;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.Mcp;

/// <summary>
/// The limits per key and for the whole port, what counts as a change, the upload link's address and name, the file routes and
/// the skill, the held handles per key, the cursors bound to their tool, and the names of access keys.
/// </summary>
public sealed class McpLimitsAndLinksTests
{
    private static async Task<string> SecondKeyAsync(McpTestServer server, string name = "other") =>
        await server.CreateKeyAsync(name: name);

    [Fact]
    public async Task EveryToolThatIsNoPlainReadCountsAsAChangeAndEveryKeyHasItsOwnBudget()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        var other = await SecondKeyAsync(server);
        static JsonObject Arguments() => new() { ["handle"] = "none" };

        // A tag run is no task run: it counted for nothing before; every run of any tool counts now (the handle does not exist: still counted).
        for (var i = 0; i < McpAccess.ChangesPerMinute; i++)
        {
            Assert.Equal(200, (await server.PostAsync(key, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_tags_assign_run", ["arguments"] = Arguments() }))).Status);
        }

        var refused = await server.PostAsync(key, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_tags_assign_run", ["arguments"] = Arguments() }));
        Assert.Equal(429, refused.Status);
        Assert.Equal("60", refused.RetryAfter);
        Assert.Equal("Too many changes with this access key. Wait a minute.", refused.ErrorMessage);

        // Plain reads of the same key go on; another key has its own budget (a key is an identity of its own).
        Assert.Equal(200, (await server.PostAsync(key, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_tags_list", ["arguments"] = new JsonObject() }))).Status);
        Assert.Equal(200, (await server.PostAsync(other, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_devices_remove_run", ["arguments"] = Arguments() }))).Status);
    }

    [Fact]
    public async Task ACallTheRegistryRefusesCountsAgainstNoLimit()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await McpTestServer.StartAsync(accessClock: clock);
        var roles = new Proto.RoleService.RoleServiceClient(server.Host.Invoker);
        var saved = await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = "Readers", Permissions = { } } } });
        var role = saved.Roles.Single(r => r.Name == "Readers");
        var key = await server.CreateKeyAsync(name: "reader", roleId: role.Id);
        JsonObject Run(string tool = "oadm_tags_assign_run") => McpTestServer.Request("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = new JsonObject { ["handle"] = "none" } });

        // A role without tag rights calls a run tool twice the budget: every call is refused (a tool error, never 429) and charges nothing.
        for (var i = 0; i < 2 * McpAccess.ChangesPerMinute; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            var refused = await server.PostAsync(key, Run());
            Assert.Equal(200, refused.Status);
            Assert.Contains("Your role does not allow", refused.Body!.ToJsonString(), StringComparison.Ordinal);
        }

        // An unknown tool counts nothing either.
        for (var i = 0; i < 2 * McpAccess.ChangesPerMinute; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            await server.PostAsync(key, Run("oadm_no_such_tool"));
        }

        // With the right, the same key still has its whole budget of changes.
        await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Id = role.Id, Name = "Readers", Permissions = { HostPermissions.TagsAssign } } } });
        for (var i = 0; i < McpAccess.ChangesPerMinute; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            Assert.Equal(200, (await server.PostAsync(key, Run())).Status);
        }

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(429, (await server.PostAsync(key, Run())).Status);
    }

    [Fact]
    public async Task ASnapshotACallerWithoutLiveViewIsRefusedForCountsAgainstNoPictureLimit()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await McpTestServer.StartAsync(accessClock: clock);
        var roles = new Proto.RoleService.RoleServiceClient(server.Host.Invoker);
        var saved = await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = "Agents", Permissions = { } } } });
        var role = Guid.Parse(saved.Roles.Single(r => r.Name == "Agents").Id);
        var key = await server.CreateKeyAsync(await server.Host.InvokerForRoleAsync("agent2", role));
        JsonObject Snapshot() => McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_devices_snapshot", ["arguments"] = new JsonObject { ["device_id"] = Guid.NewGuid().ToString() } });

        for (var i = 0; i < 2 * SnapshotAudit.PicturesPerMinute; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            var refused = await server.PostAsync(key, Snapshot());
            Assert.Equal(200, refused.Status); // refused by the role, never 429
            Assert.Contains("Your role does not allow", refused.Body!.ToJsonString(), StringComparison.Ordinal);
        }

        // Give the role Live view: the user's whole budget of pictures is still there.
        await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Id = role.ToString(), Name = "Agents", Permissions = { HostPermissions.DevicesLiveView } } } });
        for (var i = 0; i < SnapshotAudit.PicturesPerMinute; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            Assert.Equal(200, (await server.PostAsync(key, Snapshot())).Status);
        }

        clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(429, (await server.PostAsync(key, Snapshot())).Status);
    }

    [Fact]
    public async Task SnapshotsHaveALimitOfTheirOwnPerKeyAndTheThirtyFirstInAMinuteIs429()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await McpTestServer.StartAsync(accessClock: clock);
        var key = await server.CreateKeyAsync();
        var other = await SecondKeyAsync(server);
        JsonObject Snapshot() => McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_devices_snapshot", ["arguments"] = new JsonObject { ["device_id"] = Guid.NewGuid().ToString() } });

        // The device does not exist: the call is still counted (it is audited when it works, and the limit must not depend on that).
        for (var i = 0; i < SnapshotAudit.PicturesPerMinute; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1)); // under the calls a second, inside the minute
            Assert.Equal(200, (await server.PostAsync(key, Snapshot())).Status);
        }

        clock.Advance(TimeSpan.FromSeconds(1));
        var refused = await server.PostAsync(key, Snapshot());
        Assert.Equal(429, refused.Status);
        Assert.Equal("60", refused.RetryAfter);
        Assert.Equal("Too many pictures. Wait a minute.", refused.ErrorMessage);

        // Plain reads, another key and a later minute are not affected; a picture is no change either.
        Assert.Equal(200, (await server.PostAsync(key, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_server_info", ["arguments"] = new JsonObject() }))).Status);
        Assert.Equal(200, (await server.PostAsync(other, Snapshot())).Status);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(200, (await server.PostAsync(key, Snapshot())).Status);
    }

    [Fact]
    public async Task ARefusedCallChargesNoCounter()
    {
        await using var host = await TestServerHost.StartAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var keys = new AccessKeyStore(host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<Oadm.Core.Persistence.OadmDbContext>>(), time);
        var (one, _) = await keys.CreateAsync("one", BuiltInRoles.AdministratorId, DeviceScope.All, 30, "admin", CancellationToken.None);
        var (two, _) = await keys.CreateAsync("two", BuiltInRoles.AdministratorId, DeviceScope.All, 30, "admin", CancellationToken.None);
        using var access = new McpAccess(keys, host.Get<Oadm.Core.Auth.RoleStore>(), host.Get<Oadm.Core.DeviceGroups.DeviceViewFactory>(), host.Get<AccessEpochs>(), host.Get<AuditLog>(), time);
        var first = (await access.AuthenticateAsync(one, "local", CancellationToken.None))!;
        var second = (await access.AuthenticateAsync(two, "local", CancellationToken.None))!;

        for (var i = 0; i < McpAccess.ChangesPerMinute; i++)
        {
            Assert.True(access.TryChange(first));
        }

        // Refused calls of the first key charge nothing: after its minute it has its whole budget again, and the second key never lost any.
        for (var i = 0; i < 20; i++)
        {
            Assert.False(access.TryChange(first));
        }

        for (var i = 0; i < McpAccess.ChangesPerMinute; i++)
        {
            Assert.True(access.TryChange(second), "change " + i);
        }

        Assert.False(access.TryChange(second));
        time.Advance(TimeSpan.FromMinutes(1));
        for (var i = 0; i < McpAccess.ChangesPerMinute; i++)
        {
            Assert.True(access.TryChange(first), "after the minute " + i);
        }
    }

    [Fact]
    public async Task FileTransfersHaveTheirOwnPlacesAndNeverTakeThoseOfMcpCalls()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        var otherUser = await SecondKeyAsync(server);
        var access = server.Host.Get<McpAccess>();
        var caller = (await access.AuthenticateAsync(key, "local", CancellationToken.None))!;
        var link = await Link(server, key, "a.bin", 3);

        // One user holds all its transfer places (stalled bodies hold them just so): its next PUT and GET are refused, and the other user and /mcp go on.
        var held = Enumerable.Range(0, McpAccess.TransfersAtOnce).Select(_ => access.TryStartTransfer(caller)!).ToList();
        Assert.All(held, Assert.NotNull);
        Assert.Null(access.TryStartTransfer(caller));
        using var client = server.Http();
        using var put = Put(link["url"]!.GetValue<string>(), key);
        using var refusedPut = await client.SendAsync(put);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, refusedPut.StatusCode);
        using var get = Get(McpEndpoint.FilesPath + "anything", key);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, (await client.SendAsync(get)).StatusCode);
        Assert.Equal(200, (await server.PostAsync(key, McpTestServer.Request("tools/list"))).Status); // /mcp keeps its places also for the key that holds the transfers
        Assert.Equal(200, (await server.PostAsync(otherUser, McpTestServer.Request("tools/list"))).Status);
        using var elsewhere = Get(McpEndpoint.FilesPath + "anything", otherUser);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.SendAsync(elsewhere)).StatusCode); // reaches the link check

        // The port: the places of 8 transfers taken by many users, then nobody transfers, and every /mcp call still answers.
        foreach (var place in held)
        {
            place.Dispose();
        }

        var port = new List<IDisposable>();
        for (var i = 0; i < McpAccess.TransfersInPort; i++)
        {
            var someone = caller with { User = caller.User with { UserId = Guid.NewGuid() }, BindingKey = ToolHandles.BindingKeyOf(Guid.NewGuid(), "t" + i) };
            port.Add(access.TryStartTransfer(someone)!);
        }

        Assert.All(port, Assert.NotNull);
        using var late = Get(McpEndpoint.FilesPath + "anything", otherUser);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, (await client.SendAsync(late)).StatusCode);
        Assert.Equal(200, (await server.PostAsync(otherUser, McpTestServer.Request("tools/list"))).Status);
        Assert.Equal(200, (await server.PostAsync(key, McpTestServer.Request("tools/list"))).Status);
        foreach (var place in port)
        {
            place.Dispose();
        }

        using var free = Get(McpEndpoint.FilesPath + "anything", key);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await client.SendAsync(free)).StatusCode); // places are given back
    }

    [Fact]
    public void AFileTransferAsksKestrelForAMinimumDataRate()
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var body = new RateFeature();
        var response = new ResponseRateFeature();
        http.Features.Set<Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinRequestBodyDataRateFeature>(body);
        http.Features.Set<Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinResponseDataRateFeature>(response);
        McpEndpoint.RequireTransferRate(http);
        Assert.Equal(McpEndpoint.TransferMinBytesPerSecond, body.MinDataRate!.BytesPerSecond);
        Assert.Equal(McpEndpoint.TransferGrace, body.MinDataRate.GracePeriod);
        Assert.Equal(McpEndpoint.TransferMinBytesPerSecond, response.MinDataRate!.BytesPerSecond);
        Assert.Equal(1024, McpEndpoint.TransferMinBytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(10), McpEndpoint.TransferGrace);
    }

    private sealed class RateFeature : Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinRequestBodyDataRateFeature
    {
        public Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate? MinDataRate { get; set; }
    }

    private sealed class ResponseRateFeature : Microsoft.AspNetCore.Server.Kestrel.Core.Features.IHttpMinResponseDataRateFeature
    {
        public Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate? MinDataRate { get; set; }
    }

    [Fact]
    public async Task ARequestToAFilesLinkLeavesNoLogLineWithTheTokenInIt()
    {
        var logs = new CapturingLoggerFactory();
        await using var server = await McpTestServer.StartAsync(configureServices: s => s.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(logs));
        var key = await server.CreateKeyAsync();
        var link = await Link(server, key, "a.bin", 3);
        var token = new Uri(link["url"]!.GetValue<string>()).AbsolutePath[McpEndpoint.FilesPath.Length..];
        using var client = server.Http();
        using var put = Put(link["url"]!.GetValue<string>(), key);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.SendAsync(put)).StatusCode);
        using var get = Get(McpEndpoint.FilesPath + token, key);
        await client.SendAsync(get);

        Assert.True(logs.Count > 0, "the capturing factory is the one the server uses");
        // The web host's own categories (Hosting, Routing, Kestrel) log the request path at Information; the MCP SDK's (capped at Warning by the server's log settings) are not the subject.
        Assert.DoesNotContain(logs.Lines, l => l.StartsWith("Microsoft.", StringComparison.Ordinal) && l.Contains(token, StringComparison.Ordinal));
    }

    private sealed class CapturingLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public int Count => _lines.Count;

        public IReadOnlyList<string> Lines => [.. _lines];

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Capture(categoryName, _lines);

        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Capture(string category, System.Collections.Concurrent.ConcurrentQueue<string> lines) : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                lines.Enqueue(category + " scope " + state);
                return null;
            }

            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue(category + " " + formatter(state, exception) + " " + state);
        }
    }

    [Fact]
    public async Task AFileTransferThatKeepsUpTheMinimumRateStillEndsAfterItsBudgetAndFreesItsPlace()
    {
        var clock = new TimerCountingClock(DateTimeOffset.UtcNow);
        await using var server = await McpTestServer.StartAsync(configureServices: services =>
        {
            services.RemoveAll<McpFiles>();
            services.AddSingleton(sp => new McpFiles(sp.GetRequiredService<ToolHandles>(), sp.GetRequiredService<Oadm.Server.Auth.InProcessCalls>(), sp.GetRequiredService<AuditLog>(), clock));
        });
        var key = await server.CreateKeyAsync();
        var access = server.Host.Get<McpAccess>();
        var caller = (await access.AuthenticateAsync(key, "local", CancellationToken.None))!;
        var link = await Link(server, key, "slow.bin", 3);
        using var client = server.Http();
        using var stalled = new StalledContent(3);
        using var put = new HttpRequestMessage(HttpMethod.Put, new Uri(link["url"]!.GetValue<string>()).PathAndQuery) { Content = stalled };
        put.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        // The body sends a byte and then waits: a trickle. Its budget is 2 minutes plus the size at 100 KB/s; the fake clock runs it out.
        Assert.Equal(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(3 / McpFiles.TransferBudgetBytesPerSecond)), McpFiles.TransferBudget(3));
        var sending = client.SendAsync(put);
        try
        {
            // The budget's timer starts with the transfer: only then does the fake clock run it out.
            await Oadm.Sdk.Testing.Eventually.TrueAsync(() => clock.Timers > 0 || sending.IsCompleted, "the transfer started its budget");
            clock.Advance(TimeSpan.FromMinutes(3));
            using var response = await sending.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(System.Net.HttpStatusCode.RequestTimeout, response.StatusCode);
            Assert.Contains(McpFiles.TransferTooLong, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            stalled.Release();
        }

        // Its place is free again: the user can hold all of its places.
        var held = Enumerable.Range(0, McpAccess.TransfersAtOnce).Select(_ => access.TryStartTransfer(caller)).ToList();
        Assert.All(held, h => Assert.NotNull(h));
        foreach (var place in held)
        {
            place!.Dispose();
        }

        Assert.Equal(TimeSpan.FromMinutes(15), McpFiles.TransferBudget(null)); // a download of unknown size
    }

    /// <summary>A fake clock that counts the timers made on it (the transfer's budget is the only one of <see cref="McpFiles"/>).</summary>
    private sealed class TimerCountingClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private int _timers;

        public int Timers => Volatile.Read(ref _timers);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            // Counted only once the timer is registered: advancing the clock before that would start its due time later.
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);
            return timer;
        }
    }

    /// <summary>A request body that sends one byte and then waits until released.</summary>
    private sealed class StalledContent : HttpContent
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StalledContent(long length) => Headers.ContentLength = length;

        public void Release() => _release.TrySetResult();

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            await stream.WriteAsync(new byte[] { 1 }).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            await _release.Task.ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = Headers.ContentLength ?? 0;
            return true;
        }
    }

    [Fact]
    public async Task AFileRequestKeepsTheOneMegabyteBodyLimitUntilItsUploadLinkIsTakenThenExactlyTheLinksSize()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        var size = (int)McpEndpoint.MaxBodyBytes + 1;
        var link = await Link(server, key, "large.bin", size);
        var path = new Uri(link["url"]!.GetValue<string>()).AbsolutePath;
        var body = new byte[size];

        var refused = await GuardPutAsync(server, "oadm_unknownkey", path, body);
        Assert.Equal(401, refused.Status);
        Assert.Equal(McpEndpoint.MaxBodyBytes, refused.Limit);

        var other = await GuardPutAsync(server, key, "/files/notalink", body);
        Assert.Equal(404, other.Status);
        Assert.Equal(McpEndpoint.MaxBodyBytes, other.Limit);

        var taken = await GuardPutAsync(server, key, path, body);
        Assert.Equal(200, taken.Status);
        Assert.Equal(size, taken.Limit);
    }

    /// <summary>A PUT through the guard with a body size feature of its own (the in-memory test server has none to read back).</summary>
    private static async Task<(int Status, long? Limit)> GuardPutAsync(McpTestServer server, string key, string path, byte[] body)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = server.Host.Services };
        var limit = new BodyLimit();
        http.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>(limit);
        http.Request.Method = "PUT";
        http.Request.Scheme = "https";
        http.Request.Host = new Microsoft.AspNetCore.Http.HostString("localhost");
        http.Request.Path = path;
        http.Request.Headers.Authorization = "Bearer " + key;
        http.Request.ContentLength = body.Length;
        http.Request.Body = new MemoryStream(body);
        http.Response.Body = new MemoryStream();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
        await McpEndpoint.GuardAsync(http, _ => throw new InvalidOperationException("Only /mcp goes on."), server.Host.Get<McpAccess>(), null,
            server.Host.Get<McpFiles>(), server.Host.Get<ToolRegistry>(), null);
        return (http.Response.StatusCode, limit.MaxRequestBodySize);
    }

    private sealed class BodyLimit : Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;

        public long? MaxRequestBodySize { get; set; } = 30_000_000; // Kestrel's default
    }

    private static HttpRequestMessage Put(string url, string key)
    {
        var put = new HttpRequestMessage(HttpMethod.Put, new Uri(url).PathAndQuery) { Content = new ByteArrayContent([1, 2, 3]) };
        put.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        return put;
    }

    private static HttpRequestMessage Get(string path, string key)
    {
        var get = new HttpRequestMessage(HttpMethod.Get, path);
        get.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        return get;
    }

    [Fact]
    public async Task AKeyHasItsCallsASecondAndThePortHasOneCapForEveryone()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow); // stands still: every call falls into the same second
        await using var server = await McpTestServer.StartAsync(accessClock: clock);
        var key = await server.CreateKeyAsync();
        var otherUser = await SecondKeyAsync(server);

        // 20 calls a second per key: the 21st is refused, another key is not.
        for (var i = 0; i < McpAccess.CallsPerSecond; i++)
        {
            Assert.Equal(200, (await server.PostAsync(key, McpTestServer.Request("tools/list"))).Status);
        }

        var refused = await server.PostAsync(key, McpTestServer.Request("tools/list"));
        Assert.Equal(429, refused.Status);
        Assert.Equal(McpEndpoint.TooManyCalls, refused.ErrorMessage);

        Assert.Equal(200, (await server.PostAsync(otherUser, McpTestServer.Request("tools/list"))).Status);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(200, (await server.PostAsync(key, McpTestServer.Request("tools/list"))).Status); // the next second

        // The port: more places than one user has are taken by many users; the next call of anyone is refused.
        var access = server.Host.Get<McpAccess>();
        var caller = (await access.AuthenticateAsync(otherUser, "local", CancellationToken.None))!;
        var held = new List<IDisposable>();
        for (var i = 0; i < McpAccess.CallsInPort; i++)
        {
            var someone = caller with { User = caller.User with { UserId = Guid.NewGuid() }, BindingKey = ToolHandles.BindingKeyOf(Guid.NewGuid(), "k" + i) };
            held.Add(access.TryStartCall(someone)!);
        }

        Assert.All(held, Assert.NotNull);
        Assert.Null(access.TryStartCall(caller with { User = caller.User with { UserId = Guid.NewGuid() }, BindingKey = ToolHandles.BindingKeyOf(Guid.NewGuid(), "late") }));
        held[0].Dispose();
        using var free = access.TryStartCall(caller with { User = caller.User with { UserId = Guid.NewGuid() }, BindingKey = ToolHandles.BindingKeyOf(Guid.NewGuid(), "after") });
        Assert.NotNull(free);
        foreach (var place in held)
        {
            place.Dispose();
        }
    }

    [Fact]
    public async Task ACallIsCountedBeforeTheRoleAndTheDeviceViewAreRead()
    {
        await using var host = await TestServerHost.StartAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var keys = new AccessKeyStore(host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<Oadm.Core.Persistence.OadmDbContext>>(), time);
        var (key, _) = await keys.CreateAsync("laptop", BuiltInRoles.AdministratorId, DeviceScope.All, 30, "admin", CancellationToken.None);
        using var access = new McpAccess(keys, host.Get<Oadm.Core.Auth.RoleStore>(), host.Get<Oadm.Core.DeviceGroups.DeviceViewFactory>(), host.Get<AccessEpochs>(), host.Get<AuditLog>(), time);
        for (var i = 0; i < McpAccess.CallsPerSecond; i++)
        {
            Assert.NotNull((await access.SignInAsync(key, "10.9.0.1", CancellationToken.None)).Caller);
        }

        var over = await access.SignInAsync(key, "10.9.0.1", CancellationToken.None);
        Assert.True(over.Throttled);
        Assert.Null(over.Caller); // refused with the key only: no role, no device view
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.NotNull((await access.SignInAsync(key, "10.9.0.1", CancellationToken.None)).Caller);
    }

    [Fact]
    public async Task TheUploadLinkUsesTheServersOwnNameAndPortNeverTheHostHeaderAndTheNameIsSafeInAShell()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var server = await McpTestServer.StartAsync(accessClock: clock);
        var key = await server.CreateKeyAsync();
        var link = await Link(server, key, "firmware (2).bin", 3, new Dictionary<string, string?> { ["Host"] = "attacker.example:1" });
        Assert.StartsWith("https://localhost:5082/files/", link["url"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal($"curl --cacert oadm-ca.pem -H \"Authorization: Bearer $OADM_KEY\" -T \"firmware (2).bin\" {link["url"]!.GetValue<string>()}", link["command"]!.GetValue<string>());

        foreach (var bad in new[] { "a\"b.bin", "a$(x).bin", "a`x`.bin", "a;b", "a'b", "../a.bin", "a/b", "a\\b", "..", "a&b", "a!b" })
        {
            clock.Advance(TimeSpan.FromMinutes(2)); // a link is a change: a minute apart
            var reply = await server.PostAsync(key, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_files_upload_link", ["arguments"] = new JsonObject { ["name"] = bad, ["size"] = 3 } }));
            var result = reply.Body!["result"]!;
            Assert.True(result["isError"]!.GetValue<bool>(), bad);
            var text = result["content"]![0]!["text"]!.GetValue<string>();
            Assert.True(text.Contains("letters, digits, spaces", StringComparison.Ordinal), bad + " -> " + text);
        }
    }

    [Fact]
    public void TheLinkNamesTheFirstDnsNameBeforeAnAddress()
    {
        Assert.Equal("https://oadm.example:5082/files/", McpEndpoint.FilesUrlOf(["10.0.0.17", "oadm.example"], 5082));
        Assert.Equal("https://10.0.0.17:5090/files/", McpEndpoint.FilesUrlOf(["10.0.0.17"], 5090));
        Assert.Equal("https://[fd00::1]:5082/files/", McpEndpoint.FilesUrlOf(["fd00::1"], 5082));
        Assert.Null(McpEndpoint.FilesUrlOf([], 5082));
        Assert.Null(McpEndpoint.FilesUrlOf(["oadm.example"], null));
    }

    [Fact]
    public async Task AnyValidKeyReadsTheSkillWhateverItsRoleAllows()
    {
        await using var server = await McpTestServer.StartAsync();
        var roles = new Proto.RoleService.RoleServiceClient(server.Host.Invoker);
        var saved = await roles.SaveRolesAsync(new Proto.SaveRolesRequest { Roles = { new Proto.RoleChange { Name = "Agents", Permissions = { } } } });
        var role = saved.Roles.Single(r => r.Name == "Agents");
        var key = await server.CreateKeyAsync(roleId: role.Id);
        Assert.NotEmpty(await ResourceUrisAsync(server, key));
        var read = await server.PostAsync(key, McpTestServer.Request("resources/read", new JsonObject { ["uri"] = McpSkill.UriOf(McpSkill.Files.Keys.First()) }),
            new Dictionary<string, string?> { ["Mcp-Name"] = McpSkill.UriOf(McpSkill.Files.Keys.First()) });
        Assert.Null(read.Body?["error"]);
    }

    [Fact]
    public void HandlesAreCappedPerUserOverAllTheirKeysAndByHeldSizeTheOldestFirst()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var handles = new ToolHandles(time);
        var user = Guid.NewGuid();
        var first = handles.AddRun(ToolHandles.BindingKeyOf(user, "k0"), new PlannedRun("p", ["a"], null, "t"));
        var tokens = new List<string>();
        for (var key = 1; key <= 4; key++)
        {
            for (var i = 0; i < ToolHandles.MaxPerCaller; i++)
            {
                tokens.Add(handles.AddRun(ToolHandles.BindingKeyOf(user, "k" + key), new PlannedRun("p", ["a"], null, "t")));
            }
        }

        // 1 + 80 made, 60 kept for the user, the oldest gone; another user is not touched.
        Assert.Null(handles.TakeRun(ToolHandles.BindingKeyOf(user, "k0"), first));
        Assert.Null(handles.TakeRun(ToolHandles.BindingKeyOf(user, "k1"), tokens[0]));
        Assert.NotNull(handles.TakeRun(ToolHandles.BindingKeyOf(user, "k4"), tokens[^1]));
        var other = Guid.NewGuid();
        var theirs = handles.AddRun(ToolHandles.BindingKeyOf(other, "k"), new PlannedRun("p", ["a"], null, "t"));
        Assert.NotNull(handles.TakeRun(ToolHandles.BindingKeyOf(other, "k"), theirs));

        // Size: plans of 100,000 device ids (about 4 MB each) held by one user stay within 16 MB.
        var ids = Enumerable.Range(0, 100_000).Select(i => Guid.NewGuid().ToString()).ToList();
        var big = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            big.Add(handles.AddRun(ToolHandles.BindingKeyOf(user, "big" + i), new PlannedRun("p", ids, null, "t")));
        }

        Assert.Null(handles.TakeRun(ToolHandles.BindingKeyOf(user, "big0"), big[0])); // the oldest went to make room
        Assert.NotNull(handles.TakeRun(ToolHandles.BindingKeyOf(user, "big5"), big[5]));
    }

    [Fact]
    public async Task ACursorOfOneListIsRefusedByAnotherListWithTheSameArguments()
    {
        await using var server = await McpTestServer.StartAsync();
        var key = await server.CreateKeyAsync();
        foreach (var name in new[] { "Alpha", "Beta", "Gamma" })
        {
            await server.Host.Tags.CreateAsync(new Proto.CreateTagRequest { Name = name });
        }

        var page = await server.ToolAsync(key, "oadm_tags_list", new JsonObject { ["limit"] = 1 });
        var cursor = page["next_cursor"]!.GetValue<string>();
        var again = await server.ToolAsync(key, "oadm_tags_list", new JsonObject { ["limit"] = 1, ["cursor"] = cursor });
        Assert.NotNull(again["rows"] ?? again["tags"]);

        // The same arguments and cursor in a different list tool: refused, and for the logs never parsed.
        foreach (var tool in new[] { "oadm_sites_list", "oadm_logs_audit", "oadm_logs_search", "oadm_events_log", "oadm_rules_list" })
        {
            var result = await server.CallToolAsync(key, tool, new JsonObject { ["limit"] = 1, ["cursor"] = cursor });
            Assert.True(result["isError"]!.GetValue<bool>(), tool);
            Assert.Contains(ToolCursors.NotValid, result["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AServerLogCursorWithTextInsteadOfANumberIsNeverParsedAsOne()
    {
        await using var server = await McpTestServer.StartAsync();
        var cursors = server.Host.Get<ToolCursors>();
        var key = await server.CreateKeyAsync();
        var caller = (await server.Host.Get<McpAccess>().AuthenticateAsync(key, "local", CancellationToken.None))!;
        var call = new ToolCall(caller, System.Text.Json.JsonDocument.Parse("{}").RootElement, null!, CancellationToken.None) { Tool = "oadm_logs_search" };
        var text = cursors.Make(caller, ToolCursors.QueryOf(call), "2026-01-01T00:00:00.000Z", "not a number");
        var reply = await server.CallToolAsync(key, "oadm_logs_search", new JsonObject { ["cursor"] = text });
        Assert.False(reply["isError"]?.GetValue<bool>() ?? false, reply.ToJsonString()); // read as "no lines shown yet", never an exception
    }

    [Theory]
    [InlineData("a$(x)")]
    [InlineData("name\"quoted")]
    [InlineData("under`tick")]
    [InlineData("semi;colon")]
    [InlineData("new/line")]
    public async Task AnAccessKeyNameAllowsOnlyLettersDigitsSpaceDashUnderscoreAndDot(string name)
    {
        await using var server = await McpTestServer.StartAsync();
        var refused = await Assert.ThrowsAsync<Grpc.Core.RpcException>(async () => await server.CreateKeyAsync(name: name));
        Assert.Equal(Grpc.Core.StatusCode.InvalidArgument, refused.StatusCode);
        Assert.Equal(AccessKeyTexts.NameCharacters, refused.Status.Detail);
        await server.CreateKeyAsync(name: "Laptop 2.0_a-b"); // fine
        Assert.Equal("Old name_", AccessKeyStore.CleanName("Old name$"));
    }

    private static async Task<JsonObject> Link(McpTestServer server, string key, string name, int size, IReadOnlyDictionary<string, string?>? headers = null)
    {
        var reply = await server.PostAsync(key, McpTestServer.Request("tools/call", new JsonObject { ["name"] = "oadm_files_upload_link", ["arguments"] = new JsonObject { ["name"] = name, ["size"] = size } }), headers);
        Assert.Equal(200, reply.Status);
        return (JsonObject)reply.Body!["result"]!["structuredContent"]!;
    }

    private static async Task<List<string>> ResourceUrisAsync(McpTestServer server, string key)
    {
        var listed = await server.PostAsync(key, McpTestServer.Request("resources/list"));
        return [.. listed.Body!["result"]!["resources"]!.AsArray().Select(r => r!["uri"]!.GetValue<string>())];
    }
}
