using System.Collections.Concurrent;
using System.Net;
using System.Text;

using Microsoft.Extensions.Time.Testing;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Autofocus.Tests;

/// <summary>The Autofocus task against recorded multisensor answers: every lens in turn, each its own step.</summary>
public sealed class AutofocusTaskPluginTests
{
    private const string FourLenses = "optics-capabilities-P3735-PLE-13.0.114.json";
    private const string TwoLenses = "optics-capabilities-P4708-PLVE-12.11.147.json";
    private const string FocusFound = """{"apiVersion":"1.3","method":"performAutofocus","data":{}}""";
    private const string NotFound = """{"apiVersion":"1.3","method":"performAutofocus","error":{"code":1300,"message":"Focus not found"}}""";

    private static readonly DeviceApi Optics = new("optics-control", "1.3");

    [Fact]
    public void Identity_CanRun_and_the_reason()
    {
        var plugin = new AutofocusTaskPlugin();
        Assert.Equal("oadm.autofocus", plugin.Id);
        Assert.Equal("Autofocus", plugin.DisplayName);
        Assert.Equal("Autofocus", plugin.GetTaskName(null));
        Assert.Equal(TaskGroups.Maintenance, plugin.Group);
        Assert.Equal("focus", plugin.IconKey);
        Assert.False(plugin.RequiresDialog);
        Assert.True(plugin.DisplayName.Length <= TaskPluginNames.MaxDisplayNameLength);

        Assert.True(plugin.CanRun(new FakeDevice { Apis = [Optics] }));
        Assert.False(plugin.CanRun(new FakeDevice { Apis = [new DeviceApi("optics-control", "2.0")] })); // another major: another API
        Assert.False(plugin.CanRun(new FakeDevice { Apis = [new DeviceApi("param-cgi", "1.0")] }));
        Assert.False(plugin.CanRun(new FakeDevice { Apis = [Optics], Status = DeviceStatus.CredentialsRequired }));
        Assert.Equal("Needs remote focus (this device has 12.11.77)", plugin.NotSupportedReason(new FakeDevice { Apis = [new DeviceApi("param-cgi", "1.0")] }));
    }

    [Fact]
    public async Task Every_lens_of_a_multisensor_camera_is_focused_in_turn()
    {
        var camera = new OpticsCamera(Read(FourLenses));
        var ctx = new FakeTaskContext(camera);

        await RunAsync(ctx);

        Assert.Equal(
        [
            "Check the lenses: Done", "Focus camera 1: Done", "Focus camera 2: Done", "Focus camera 3: Done", "Focus camera 4: Done", "Completed: Done",
        ], StepRun.Lines(ctx.Steps));
        Assert.Equal("4 lenses", StepRun.Detail(ctx.Steps, "Check the lenses"));
        Assert.Equal(["0", "1", "2", "3"], camera.Focused);
        Assert.Empty(ctx.Warnings);
    }

    [Fact]
    public async Task A_lens_without_focus_is_a_warning_and_the_others_still_focus()
    {
        var camera = new OpticsCamera(Read(TwoLenses)) { Answer = id => id == "0" ? NotFound : FocusFound };
        var ctx = new FakeTaskContext(camera);

        await RunAsync(ctx);

        Assert.Equal(["Check the lenses: Done", "Focus camera 1: Warning", "Focus camera 2: Done", "Completed: Done"], StepRun.Lines(ctx.Steps));
        Assert.Equal(["0", "1"], camera.Focused);
        Assert.Equal(["The camera could not find focus. Set focus by hand."], ctx.Warnings);
    }

    [Fact]
    public async Task Without_the_api_in_the_fresh_list_nothing_is_sent()
    {
        var camera = new OpticsCamera(Read(TwoLenses)) { Apis = [new DeviceApi("optics-control", "2.0")] };
        var ctx = new FakeTaskContext(camera);

        await Assert.ThrowsAsync<DeviceNotCompatibleException>(() => RunAsync(ctx));

        Assert.DoesNotContain(camera.Requests, r => r.Contains("opticscontrol", StringComparison.Ordinal));
        Assert.Equal(["Check the lenses: Failed"], StepRun.Lines(ctx.Steps));
    }

    [Fact]
    public async Task A_fixed_lens_ends_with_a_warning_and_no_autofocus()
    {
        var camera = new OpticsCamera("""{"apiVersion":"1.3","method":"getCapabilities","data":{"numberOfOptics":1,"optics":[{"opticsId":"0","capabilities":["irCutFilter"]}]}}""");
        var ctx = new FakeTaskContext(camera);

        await RunAsync(ctx);

        Assert.Equal(["Check the lenses: Warning", "Completed: Done"], StepRun.Lines(ctx.Steps));
        Assert.Equal([AutofocusTaskPlugin.NoRemoteFocus], ctx.Warnings);
        Assert.Empty(camera.Focused);
    }

    [Fact]
    public async Task A_refused_request_fails_the_task()
    {
        var camera = new OpticsCamera(Read(TwoLenses)) { FailWith = HttpStatusCode.InternalServerError };
        var ctx = new FakeTaskContext(camera);

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunAsync(ctx));

        Assert.Equal("Check the lenses: Failed", StepRun.Lines(ctx.Steps)[0]);
    }

    [Fact]
    public async Task A_lens_that_never_answers_ends_after_its_timeout_and_the_next_one_runs()
    {
        var time = new FakeTimeProvider();
        var camera = new OpticsCamera(Read(TwoLenses)) { Hang = id => id == "0" };
        var ctx = new FakeTaskContext(camera);
        var run = StepRun.RunAsync(ctx.Steps, () => new AutofocusTaskPlugin(TimeSpan.FromMinutes(2), time).ExecuteAsync(ctx, new FakeDevice { Apis = [Optics] }, null, CancellationToken.None));

        await Eventually.TrueAsync(() => camera.Waiting == 1, "the first lens searches");
        time.Advance(TimeSpan.FromMinutes(2));
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["Check the lenses: Done", "Focus camera 1: Warning", "Focus camera 2: Done", "Completed: Done"], StepRun.Lines(ctx.Steps));
        Assert.Equal(["The autofocus did not end within 2 minutes."], ctx.Warnings);
    }

    private static Task RunAsync(FakeTaskContext ctx) =>
        StepRun.RunAsync(ctx.Steps, () => new AutofocusTaskPlugin().ExecuteAsync(ctx, new FakeDevice { Apis = [Optics] }, null, CancellationToken.None));

    private static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>A camera with the Optics Control API: getCapabilities from a recording, performAutofocus per lens.</summary>
    private sealed class OpticsCamera : FakeCamera
    {
        private readonly string _capabilities;
        private readonly ConcurrentQueue<string> _focused = new();
        private int _waiting;

        public OpticsCamera(string capabilities)
        {
            _capabilities = capabilities;
            Apis = [new DeviceApi("optics-control", "1.3")];
        }

        public Func<string, string> Answer { get; init; } = _ => FocusFound;

        public Func<string, bool> Hang { get; init; } = _ => false;

        public HttpStatusCode? FailWith { get; init; }

        public IReadOnlyList<string> Focused => [.. _focused];

        public int Waiting => Volatile.Read(ref _waiting);

        public override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            _ = await base.SendAsync(request, ct); // records the request
            if (FailWith is { } status)
            {
                return new HttpResponseMessage(status) { Content = new StringContent("") };
            }

            var body = await request.Content!.ReadAsStringAsync(ct);
            if (body.Contains("getCapabilities", StringComparison.Ordinal))
            {
                return Json(_capabilities);
            }

            var id = System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("params").GetProperty("optics")[0].GetProperty("opticsId").GetString()!;
            _focused.Enqueue(id);
            if (Hang(id))
            {
                Interlocked.Increment(ref _waiting);
                await Task.Delay(Timeout.Infinite, ct); // searches until the task gives up
            }

            return Json(Answer(id));
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
