using System.Collections.Concurrent;

using Oadm.Core.Auth;
using Oadm.Core.MyAxis;
using Oadm.Core.Persistence;
using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Core.Tests.DeviceGroups;
using Oadm.Core.Tests.Tasks;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.MyAxis;

/// <summary>A task gets the downloads of the user who started it (docs/spec/myaxis.md); a system task gets none.</summary>
public sealed class TaskAxisDownloadsTests : DeviceGroupTestBase
{
    private readonly PluginRegistry _registry = new();
    private readonly FakeVapixClientFactory _vapix = new();
    private readonly RecordingSource _source = new();

    private TaskEngine Engine() =>
        new(Db.Get<EfTaskStore>(), _registry, Devices, _vapix, options: new TaskEngineOptions { AxisDownloads = _source }) { Views = Views };

    [Fact]
    public async Task AUsersTaskGetsThatUsersDownloadsAndASystemTaskNone()
    {
        var device = await AddDeviceAsync("ACCC8E000001");
        var seen = new ConcurrentQueue<IAxisDownloads?>();
        Assert.True(_registry.RegisterTaskPlugin(
            new DelegateTaskPlugin("t.dl", (ctx, _, _) =>
            {
                seen.Enqueue(ctx.AxisDownloads);
                return Task.CompletedTask;
            }),
            new PluginOrigin("test.t.dl", "1.0.0", null) { MinSdkVersion = SdkInfo.Version }));
        var anna = CallerOf(await Users.AddAsync("anna", "pw", BuiltInRoles.OperatorId, DeviceScope.All, Ct));
        await using var engine = Engine();

        await engine.RunAsync("t.dl", await (await Views.ForAsync(anna, Ct)).GetAsync([device.Id], Ct), null, TaskCaller.Of(anna), Ct);
        await Eventually.TrueAsync(() => seen.Count == 1, "the user's task ran");
        await engine.RunAsync("t.dl", [device.Id], null, "system", Ct);
        await Eventually.TrueAsync(() => seen.Count == 2, "the system task ran");

        var all = seen.ToArray();
        Assert.Same(_source.Given, all[0]);
        Assert.Null(all[1]);
        Assert.Equal([anna.UserId], _source.Users);
    }

    [Fact]
    public async Task TheSignInIsReadOncePerRunNotPerDevice()
    {
        var devices = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            devices.Add((await AddDeviceAsync($"ACCC8E00001{i}")).Id);
        }

        var seen = new ConcurrentQueue<IAxisDownloads?>();
        Assert.True(_registry.RegisterTaskPlugin(
            new DelegateTaskPlugin("t.dl", (ctx, _, _) =>
            {
                seen.Enqueue(ctx.AxisDownloads);
                return Task.CompletedTask;
            }),
            new PluginOrigin("test.t.dl", "1.0.0", null) { MinSdkVersion = SdkInfo.Version }));
        var anna = CallerOf(await Users.AddAsync("anna", "pw", BuiltInRoles.OperatorId, DeviceScope.All, Ct));
        await using var engine = Engine();

        await engine.RunAsync("t.dl", await (await Views.ForAsync(anna, Ct)).GetAsync(devices, Ct), null, TaskCaller.Of(anna), Ct);
        await Eventually.TrueAsync(() => seen.Count == 5, "every device ran");

        Assert.All(seen, d => Assert.Same(_source.Given, d));
        Assert.Single(_source.Users);
    }

    [Fact]
    public async Task ASignInThatCannotBeReadNeverFailsATask()
    {
        var device = await AddDeviceAsync("ACCC8E000001");
        var seen = new ConcurrentQueue<IAxisDownloads?>();
        Assert.True(_registry.RegisterTaskPlugin(
            new DelegateTaskPlugin("t.dl", (ctx, _, _) =>
            {
                seen.Enqueue(ctx.AxisDownloads);
                return Task.CompletedTask;
            }),
            new PluginOrigin("test.t.dl", "1.0.0", null) { MinSdkVersion = SdkInfo.Version }));
        var anna = CallerOf(await Users.AddAsync("anna", "pw", BuiltInRoles.OperatorId, DeviceScope.All, Ct));
        _source.Fails = true;
        await using var engine = Engine();

        var ids = await engine.RunAsync("t.dl", await (await Views.ForAsync(anna, Ct)).GetAsync([device.Id], Ct), null, TaskCaller.Of(anna), Ct);
        await engine.WaitForCompletionAsync(ids[0], Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(TaskState.Done, (await Db.Get<EfTaskStore>().GetAsync(ids[0], Ct))!.State);
        Assert.Null(Assert.Single(seen));
    }

    [Fact]
    public async Task ARunReadsTheSignInAgainAfterAMinute()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero));
        var runs = new RunAxisDownloads(_source, time, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        var batch = Guid.NewGuid();
        var user = Guid.NewGuid();

        Assert.Same(_source.Given, await runs.ForAsync(batch, user, Ct));
        Assert.Same(_source.Given, await runs.ForAsync(batch, user, Ct));
        Assert.Single(_source.Users);

        await runs.ForAsync(Guid.NewGuid(), user, Ct); // another run reads for itself
        Assert.Equal(2, _source.Users.Count);

        time.Advance(RunAxisDownloads.KeepFor);
        await runs.ForAsync(batch, user, Ct); // a sign-in during a long run counts
        Assert.Equal(3, _source.Users.Count);
    }

    private sealed class RecordingSource : IAxisDownloadSource
    {
        public IAxisDownloads Given { get; } = new NoDownloads();

        public ConcurrentQueue<Guid> Users { get; } = new();

        public bool Fails { get; set; }

        public Task<IAxisDownloads> ForUserAsync(Guid userId, CancellationToken ct)
        {
            Users.Enqueue(userId);
            return Fails ? Task.FromException<IAxisDownloads>(new InvalidOperationException("database is locked")) : Task.FromResult(Given);
        }
    }

    private sealed class NoDownloads : IAxisDownloads
    {
        public bool HasSession => false;

        public Task<AxisDownloadedFile> DownloadAsync(Uri uri, string sha256, long size, IProgress<long>? progress, CancellationToken ct) =>
            throw new AxisDownloadException(AxisDownloadTexts.SignIn) { NeedsSignIn = true };
    }
}
