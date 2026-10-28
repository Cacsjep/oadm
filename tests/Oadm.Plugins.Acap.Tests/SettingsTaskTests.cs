using Oadm.Sdk.Plugins;
using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.Acap.Tests;

/// <summary>The Change settings action: checks before the write, one update, read back; values never in any text.</summary>
public sealed class SettingsTaskTests
{
    private static readonly AcapTaskPlugin Plugin = new(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(200), TimeProvider.System);

    private const string Secret = "s3cret-value";

    private static (FakeAcapDevice Device, RecordingContext Ctx) Setup()
    {
        var device = new FakeAcapDevice();
        device.Add("demo", "1.0.0", "Running");
        device.AddSetting("demo", "Threshold", "<int min=\"0\" max=\"100\" />", "40", niceName: "Threshold");
        device.AddSetting("demo", "Mode", "<enum><entry value=\"fast\" niceValue=\"Fast\" /><entry value=\"exact\" niceValue=\"Exact\" /></enum>", "fast");
        device.AddSetting("demo", "Enabled", "<bool true=\"yes\" false=\"no\" />", "yes");
        device.AddSetting("demo", "Server", "<string maxlen=\"64\" />", "10.0.0.5", niceName: "Server address");
        device.AddSetting("demo", "Key", "<password maxlen=\"32\" />", "old-key", flags: "writeonly=\"true\"", niceName: "Pre-shared key");
        device.AddSetting("demo", "Internal", "<string />", "1", flags: "hidden=\"true\"");
        return (device, new RecordingContext(device));
    }

    private static string Payload(Dictionary<string, string> values, string app = "demo") =>
        new AcapPayload { Action = AcapAction.Settings, Application = app, AppDisplayName = "Demo App", Values = values }.ToJson();

    private static Task Run(RecordingContext ctx, string payload) =>
        StepRun.RunAsync(ctx.NewRun(), () => Plugin.ExecuteAsync(ctx, new FakeDevice(), payload, CancellationToken.None));

    /// <summary>Every text the task wrote: logs, warnings, step details.</summary>
    private static IEnumerable<string> Texts(RecordingContext ctx) =>
        ctx.Logs.Select(l => l.Message).Concat(ctx.Warnings).Concat(ctx.Steps.Snapshot().Select(s => s.Detail ?? string.Empty));

    [Fact]
    public async Task Changes_only_the_new_values_in_one_request_and_reads_them_back()
    {
        var (device, ctx) = Setup();

        await Run(ctx, Payload(new() { ["Threshold"] = "075", ["Mode"] = "fast", ["Enabled"] = "No", ["Server"] = Secret, ["Key"] = Secret + "-key" }));

        Assert.Equal(["param update"], device.Writes);
        var form = Assert.Single(device.SettingsUpdates);
        Assert.DoesNotContain("Mode", form, StringComparison.Ordinal); // unchanged: not sent
        Assert.Contains("root.demo.Threshold=75", form, StringComparison.Ordinal);
        Assert.Contains("root.demo.Enabled=no", form, StringComparison.Ordinal);
        Assert.Equal("75", device.Settings["demo"].Single(s => s.Name == "Threshold").Value);
        Assert.Equal(Secret + "-key", device.Settings["demo"].Single(s => s.Name == "Key").Value);
        Assert.Equal(
            ["Check compatibility: Done", "Read installed applications: Done", "Read settings: Done", "Change settings: Done", "Verify settings: Done", "Completed: Done"],
            StepRun.Lines(ctx.Steps));
        Assert.Equal("6 settings, 1 already set", StepRun.Detail(ctx.Steps, "Read settings"));
        Assert.Equal("Enabled, Key, Server, Threshold", StepRun.Detail(ctx.Steps, "Change settings"));
        Assert.Equal("4 settings changed, 1 password not read back", StepRun.Detail(ctx.Steps, "Verify settings"));
        Assert.DoesNotContain(Texts(ctx), t => t.Contains(Secret, StringComparison.Ordinal) || t.Contains("75", StringComparison.Ordinal));
        Assert.Empty(ctx.Warnings);
    }

    [Fact]
    public async Task Task_name_names_the_application_never_a_value()
    {
        var payload = Payload(new() { ["Server"] = Secret });

        Assert.Equal("Change settings of Demo App", Plugin.GetTaskName(payload));
        Assert.Equal([AcapPermissions.Settings], Plugin.RequiredPermissions(payload));
        Assert.DoesNotContain(Secret, Plugin.GetTaskName(payload), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Nope", "1", "Nope is not a setting of demo nice.")]
    [InlineData("Threshold", "101", "Threshold must be 0 to 100.")]
    [InlineData("Mode", "slow", "Mode must be one of its listed values.")]
    [InlineData("Enabled", "maybe", "Enabled must be yes or no.")]
    [InlineData("Internal", "2", "Internal cannot be changed.")]
    public async Task A_name_or_value_that_does_not_fit_fails_before_any_write(string name, string value, string expected)
    {
        var (device, ctx) = Setup();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, Payload(new() { ["Server"] = Secret, [name] = value })));

        Assert.Equal(expected + " Nothing was changed.", ex.Message);
        Assert.Empty(device.Writes);
        Assert.Equal(
            ["Check compatibility: Done", "Read installed applications: Done", "Read settings: Failed", "Change settings: Skipped", "Verify settings: Skipped"],
            StepRun.Lines(ctx.Steps));
        Assert.DoesNotContain(Texts(ctx), t => t.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Fails_when_the_application_is_not_installed_or_has_no_settings()
    {
        var (device, ctx) = Setup();
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, Payload(new() { ["A"] = "1" }, app: "gone")));
        Assert.Equal("Demo App is not installed on this device. Nothing was changed.", missing.Message);

        device.Add("parking", "2.0");
        var none = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, Payload(new() { ["A"] = "1" }, app: "parking")));
        Assert.Equal("parking nice has no settings here. Nothing was changed.", none.Message);
        Assert.Empty(device.Writes);
    }

    [Fact]
    public async Task Nothing_is_sent_when_every_value_is_already_set()
    {
        var (device, ctx) = Setup();

        await Run(ctx, Payload(new() { ["Threshold"] = "40", ["Mode"] = "fast" }));

        Assert.Empty(device.Writes);
        Assert.Equal("Skipped", ctx.Steps.Snapshot().Single(s => s.Name == "Change settings").State.ToString());
        Assert.Equal("Every value is already set.", StepRun.Detail(ctx.Steps, "Change settings"));
    }

    [Fact]
    public async Task A_password_is_always_sent_and_never_read_back()
    {
        var (device, ctx) = Setup();
        device.IgnoredSettings.Add("Key"); // the device masks it anyway: OADM cannot compare

        await Run(ctx, Payload(new() { ["Key"] = Secret }));

        Assert.Contains("root.demo.Key=" + Secret, Assert.Single(device.SettingsUpdates), StringComparison.Ordinal);
        Assert.Equal("1 setting changed, 1 password not read back", StepRun.Detail(ctx.Steps, "Verify settings"));
        Assert.DoesNotContain(Texts(ctx), t => t.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_value_the_device_did_not_keep_fails_the_verification()
    {
        var (device, ctx) = Setup();
        device.IgnoredSettings.Add("Server");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(ctx, Payload(new() { ["Server"] = Secret })));

        Assert.Equal("The device did not keep the new value of Server.", ex.Message);
        Assert.Equal("Verify settings: Failed", StepRun.Lines(ctx.Steps)[4]);
        Assert.DoesNotContain(Texts(ctx), t => t.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_update_never_repeats_the_value()
    {
        var (device, ctx) = Setup();
        device.UpdateReply = $"# Error: Error setting 'root.demo.Server' to '{Secret}'!";

        var ex = await Assert.ThrowsAsync<AcapSettingsException>(() => Run(ctx, Payload(new() { ["Server"] = Secret })));

        Assert.Equal("The device refused the new value of Server.", ex.Message);
        Assert.Equal("Change settings: Failed", StepRun.Lines(ctx.Steps)[3]);
        Assert.DoesNotContain(Texts(ctx), t => t.Contains(Secret, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"action":"settings","application":"demo"}""")]
    [InlineData("""{"action":"settings","application":"demo","values":{}}""")]
    [InlineData("""{"action":"settings","values":{"A":"1"}}""")]
    [InlineData("""{"action":"settings","application":"demo","values":{"a&b":"1"}}""")]
    [InlineData("""{"action":"settings","application":"demo","values":{"A":null}}""")]
    public async Task Invalid_settings_payloads_fail_without_touching_the_device(string payload)
    {
        var (device, ctx) = Setup();

        await Assert.ThrowsAsync<ArgumentException>(() => Run(ctx, payload));

        Assert.Empty(device.Requests);
        Assert.Equal(0, device.ApiListCalls);
    }

    [Fact]
    public void Payload_rejects_too_many_or_too_long_values()
    {
        var many = Enumerable.Range(0, AppSettings.MaxValues + 1).ToDictionary(i => "P" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => "1");
        Assert.Throws<ArgumentException>(() => AcapPayload.Parse(Payload(many)));
        Assert.Throws<ArgumentException>(() => AcapPayload.Parse(Payload(new() { ["A"] = new string('x', AppSettings.MaxValueLength + 1) })));
        Assert.Equal("1", AcapPayload.Parse(Payload(new() { ["A"] = "1" })).Values!["A"]);
    }

    [Fact]
    public void Declares_the_settings_permission_for_operators_too()
    {
        var permission = Assert.Single(Plugin.Permissions, p => p.Key == AcapPermissions.Settings);

        Assert.Equal("settings", permission.Key);
        Assert.Equal("Change application settings", permission.Name);
        Assert.True(permission.OperatorDefault);
        Assert.False(permission.ViewerDefault);
        Assert.Equal("task:oadm.acap:settings", PermissionKeys.Task(AcapPlugin.Id, AcapPermissions.Settings));
    }
}
