using System.Globalization;
using System.Net;

using Oadm.Core.Tests.Hardware;
using Oadm.Core.Vapix;
using Oadm.Plugins.DeviceConfig.Configure;
using Oadm.Tests.Shared;

using Xunit.Abstractions;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>
/// Opt-in write test of Configure devices on the first dev camera (dev-cameras.yaml), only with
/// <c>OADM_CONFIGURE_WRITE=1</c>: the real task sets one image parameter, creates an own schedule, a turned-off action rule
/// (its action never runs) and a stream profile; a second run must change nothing more; then everything is removed and the
/// parameter set back, and the camera must be as before.
/// </summary>
[Trait("Category", "HardwareWrite")]
public sealed class ConfigureHardwareWriteTests(ITestOutputHelper output)
{
    private const string Parameter = "Image.I0.Appearance.Compression";
    private const string TestName = "OADM test";

    public sealed class WriteFactAttribute : FactAttribute
    {
        public WriteFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("OADM_CONFIGURE_WRITE") != "1" || !DevCameras.Any)
            {
                Skip = "Set OADM_CONFIGURE_WRITE=1 and OADM_DEV_CAMERAS to write to the first dev camera.";
            }
        }
    }

    [WriteFact]
    public async Task Parameters_rules_schedules_and_profiles_are_written_once_and_removed_again()
    {
        var camera = DevCameras.All[0];
        using var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(camera.User, camera.Password),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var vapix = new VapixClient(new Uri($"{camera.EffectiveScheme}://{camera.Address}/"), handler, disposeHandler: false);
        var ct = CancellationToken.None;

        var before = await StateAsync(vapix);
        output.WriteLine($"Before: {Parameter}={before.Value}, rules [{string.Join(", ", before.Rules)}], schedules [{string.Join(", ", before.Schedules)}], profiles [{string.Join(", ", before.Profiles)}]");
        Assert.Equal(0, Count(before.Rules));
        Assert.Equal(0, Count(before.Schedules));
        Assert.Equal(0, Count(before.Profiles));

        var newValue = (int.Parse(before.Value, CultureInfo.InvariantCulture) == 30 ? 31 : 30).ToString(CultureInfo.InvariantCulture);
        var schedule = new ConfigSchedule("com.axis.schedules.genid.oadm-test", TestName, "DTSTART:19700101T000000\nRRULE:FREQ=MINUTELY;INTERVAL=30");
        var payload = new ConfigurePayload
        {
            Parameters = [new NameValue(Parameter, newValue)],
            Schedules = [schedule],
            Rules =
            [
                new ConfigActionRule(TestName, Enabled: false,
                    new ConfigEventFilter("tns1:UserAlarm/tnsaxis:Recurring/Pulse", $"boolean(//SimpleItem[@Name=\"id\" and @Value=\"{schedule.Id}\"])"),
                    [],
                    new ConfigAction("OADM test recording", "com.axis.action.fixed.recording.storage",
                        [new NameValue("stream_options", "&streamprofile=&camera=1"), new NameValue("pre_duration", "1000"), new NameValue("post_duration", "1000"), new NameValue("storage_id", "SD_DISK")])),
            ],
            StreamProfiles = [new ConfigStreamProfile(TestName, "Written by the OADM hardware test", "camera=1&resolution=640x360&fps=5&videocodec=h264")],
            Source = "hardware test",
        };

        try
        {
            var first = await RunAsync(vapix, payload);
            Assert.All(StepRun.Lines(first.Steps), line => Assert.EndsWith(": Done", line, StringComparison.Ordinal));
            var after = await StateAsync(vapix);
            output.WriteLine($"After: {Parameter}={after.Value}, rules [{string.Join(", ", after.Rules)}], schedules [{string.Join(", ", after.Schedules)}], profiles [{string.Join(", ", after.Profiles)}]");
            Assert.Equal(newValue, after.Value);
            Assert.Equal(1, Count(after.Rules));
            Assert.Equal(1, Count(after.Schedules));
            Assert.Equal(1, Count(after.Profiles));
            var rule = (await ActionEngineApi.ReadRulesAsync(vapix, ct)).Single(r => r.Name == TestName);
            Assert.False(rule.Enabled);
            var created = (await ActionEngineApi.ReadSchedulesAsync(vapix, ct)).Single(s => s.Name == TestName);
            Assert.Contains(created.Id, rule.StartEvent!.Message, StringComparison.Ordinal); // the rule points to the device's id

            // The same again: nothing is added.
            var second = await RunAsync(vapix, payload);
            Assert.Equal("1 already there", StepRun.Detail(second.Steps, ConfigureDevicesTask.Steps.CreateSchedules));
            Assert.Equal("1 replaced", StepRun.Detail(second.Steps, ConfigureDevicesTask.Steps.CreateRules));
            Assert.Equal("1 replaced", StepRun.Detail(second.Steps, ConfigureDevicesTask.Steps.SetStreamProfiles));
            var again = await StateAsync(vapix);
            Assert.Equal(1, Count(again.Rules));
            Assert.Equal(1, Count(again.Schedules));
            Assert.Equal(1, Count(again.Profiles));
        }
        finally
        {
            await CleanUpAsync(vapix, before.Value);
        }

        var restored = await StateAsync(vapix);
        output.WriteLine($"Restored: {Parameter}={restored.Value}, rules [{string.Join(", ", restored.Rules)}], schedules [{string.Join(", ", restored.Schedules)}], profiles [{string.Join(", ", restored.Profiles)}]");
        Assert.Equal(before, restored);
    }

    private sealed record State(string Value, string Rules, string Schedules, string Profiles);

    /// <summary>How often the test name is in a "|"-joined list of names.</summary>
    private static int Count(string names) => names.Split('|').Count(n => n == TestName);

    private static async Task<State> StateAsync(VapixClient vapix)
    {
        var ct = CancellationToken.None;
        var value = (await vapix.ListParametersAsync([Parameter], ct))[Parameter];
        var rules = (await ActionEngineApi.ListRuleIdsAsync(vapix, ct)).Select(r => r.Name).Order(StringComparer.Ordinal);
        var schedules = (await ActionEngineApi.ReadSchedulesAsync(vapix, ct)).Select(s => s.Name).Order(StringComparer.Ordinal);
        var profiles = (await StreamProfileApi.ListAsync(vapix, ct)).Profiles.Select(p => p.Name).Order(StringComparer.Ordinal);
        return new State(value, string.Join("|", rules), string.Join("|", schedules), string.Join("|", profiles));
    }

    private static async Task<RecordingContext> RunAsync(VapixClient vapix, ConfigurePayload payload)
    {
        var ctx = new RecordingContext(vapix, new MemoryUploads());
        await StepRun.RunAsync(ctx.Steps, () => new ConfigureDevicesTask().ExecuteAsync(ctx, new FakeDevice(), payload.ToJson(), CancellationToken.None));
        return ctx;
    }

    /// <summary>Removes the test rule (and its action), schedule and profile, and sets the parameter back.</summary>
    private async Task CleanUpAsync(VapixClient vapix, string value)
    {
        var ct = CancellationToken.None;
        foreach (var rule in (await ActionEngineApi.ListRuleIdsAsync(vapix, ct)).Where(r => r.Name == TestName))
        {
            await ActionEngineApi.RemoveRuleAsync(vapix, rule.RuleId, ct);
            if (rule.ConfigurationId is { } configuration)
            {
                await ActionEngineApi.RemoveConfigurationAsync(vapix, configuration, ct);
            }

            output.WriteLine("Removed rule " + rule.RuleId);
        }

        foreach (var schedule in (await ActionEngineApi.ReadSchedulesAsync(vapix, ct)).Where(s => s.Name == TestName))
        {
            await ActionEngineApi.RemoveScheduleAsync(vapix, schedule.Id, ct);
            output.WriteLine("Removed schedule " + schedule.Id);
        }

        if ((await StreamProfileApi.ListAsync(vapix, ct)).Profiles.Any(p => p.Name == TestName))
        {
            await StreamProfileApi.RemoveAsync(vapix, TestName, ct);
            output.WriteLine("Removed stream profile");
        }

        await ParamWriter.UpdateAsync(vapix, [new NameValue(Parameter, value)], [], ct);
        output.WriteLine($"{Parameter} set back to {value}");
    }
}
