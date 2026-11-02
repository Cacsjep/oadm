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

    private sealed class RecordingSource : IAxisDownloadSource
    {
        public IAxisDownloads Given { get; } = new NoDownloads();

        public ConcurrentQueue<Guid> Users { get; } = new();

        public Task<IAxisDownloads> ForUserAsync(Guid userId, CancellationToken ct)
        {
            Users.Enqueue(userId);
            return Task.FromResult(Given);
        }
    }

    private sealed class NoDownloads : IAxisDownloads
    {
        public bool HasSession => false;

        public Task<AxisDownloadedFile> DownloadAsync(Uri uri, string sha256, long size, IProgress<long>? progress, CancellationToken ct) =>
            throw new AxisDownloadException(AxisDownloadTexts.SignIn) { NeedsSignIn = true };
    }
}
