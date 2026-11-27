using Oadm.Plugins.LicensePlates.Tasks;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.LicensePlates.Tests;

/// <summary>
/// The tasks against a fake camera (CLAUDE.md "device safety"): ALPV read fresh before the first write, everything checked
/// first, only changed fields the camera has written and read back, refused for ALPV 2, a stopped ALPV, no license, another
/// sensor. Writes are never tested on a real camera here (HardwareWrite only, on request).
/// </summary>
public sealed class TaskTests
{
    private static readonly FakeDevice Device = new();

    private static string Payload(SetupPayload payload) => PluginJson.Serialize(payload, LicensePlatesJson.Options);

    private static async Task<(FakeTaskContext Ctx, Exception? Error)> RunAsync(ITaskPlugin task, AlpvCamera camera, string payload)
    {
        var ctx = new FakeTaskContext(camera);
        try
        {
            await StepRun.RunAsync(ctx.Steps, () => task.ExecuteAsync(ctx, Device, payload, CancellationToken.None));
            return (ctx, null);
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            return (ctx, ex);
        }
    }

    [Fact]
    public async Task Set_up_writes_only_the_changed_fields_the_camera_has_and_reads_them_back()
    {
        var camera = new AlpvCamera();
        var rechecked = new List<Guid>();
        var payload = new SetupPayload { Recognition = new RecognitionSettings { CameraLocation = "DEU", PlateWidthMin = 50, ConfidenceThreshold = 0.25 } };

        var (ctx, error) = await RunAsync(new SetupTask(rechecked.Add), camera, Payload(payload));

        Assert.Null(error);
        var write = Assert.Single(camera.AlpvWrites);
        Assert.Equal("POST /local/fflprapp/config.cgi", write.Request);
        Assert.Contains("<CountryPriority>DEU</CountryPriority>", write.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("plate_min_size", write.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("threshold", write.Body, StringComparison.Ordinal);
        Assert.Contains("<CountryPriority>DEU</CountryPriority>", camera.ConfigXml, StringComparison.Ordinal);
        Assert.Contains(ctx.LogLines, l => l.Contains("CountryPriority: AUT -> DEU", StringComparison.Ordinal));
        Assert.Equal([Device.Id], rechecked);
    }

    [Fact]
    public async Task Set_up_writes_the_region_to_lprUseCase_with_the_location_and_state()
    {
        var camera = new AlpvCamera();
        var payload = new SetupPayload { Recognition = new RecognitionSettings { Region = "19", CameraLocation = "MX", State = "CMX" } };

        var (_, error) = await RunAsync(new SetupTask(), camera, Payload(payload));

        Assert.Null(error);
        var write = Assert.Single(camera.AlpvWrites);
        Assert.Contains("<lprUseCase>19</lprUseCase>", write.Body, StringComparison.Ordinal);
        Assert.Contains("<CountryPriority>MX</CountryPriority>", write.Body, StringComparison.Ordinal);
        Assert.Contains("<StatePriority>CMX</StatePriority>", write.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("regional_settings", write.Body, StringComparison.Ordinal);
        Assert.Contains("<lprUseCase>19</lprUseCase>", camera.ConfigXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Set_up_reads_ALPV_fresh_before_the_first_write()
    {
        var camera = new AlpvCamera();

        await RunAsync(new SetupTask(), camera, Payload(new SetupPayload { Recognition = new RecognitionSettings { CameraLocation = "DEU" } }));

        var requests = camera.Requests.ToList();
        var firstWrite = requests.FindIndex(r => r.StartsWith("POST /local/fflprapp/", StringComparison.Ordinal));
        Assert.True(firstWrite > 0);
        Assert.True(requests.FindIndex(r => r.Contains("/axis-cgi/applications/list.cgi", StringComparison.Ordinal)) < firstWrite);
        Assert.True(requests.FindIndex(r => r == "GET /local/fflprapp/config.cgi") < firstWrite);
        Assert.True(requests.FindIndex(r => r == "GET /local/fflprapp/vapix_events.cgi") < firstWrite);
    }

    [Theory]
    [InlineData("2.13.4", "Running", "Valid", "ALPV 3 is needed")]
    [InlineData("3.1.7", "Stopped", "Valid", "ALPV is stopped")]
    [InlineData("3.1.7", "Running", "Missing", "ALPV has no license")]
    [InlineData(null, "Running", "Valid", "ALPV is not installed")]
    public async Task Set_up_refuses_a_camera_without_ALPV_3_running_and_licensed(string? version, string status, string license, string text)
    {
        var camera = new AlpvCamera { AppsXml = Fixture.AppsWith(version, status, license) };

        var (_, error) = await RunAsync(new SetupTask(), camera, Payload(new SetupPayload { Recognition = new RecognitionSettings { CameraLocation = "DEU" } }));

        var refused = Assert.IsType<DeviceNotCompatibleException>(error);
        Assert.Equal(text + ". Nothing was changed.", refused.Message);
        Assert.Empty(camera.AlpvWrites);
    }

    [Fact]
    public async Task Set_up_refuses_ALPV_on_another_sensor_and_areas_of_a_rotated_picture()
    {
        var other = new AlpvCamera { ConfigXml = Fixture.Config317.Replace("<videoChannelId>1</videoChannelId>", "<videoChannelId>2</videoChannelId>", StringComparison.Ordinal) };
        var (_, error) = await RunAsync(new SetupTask(), other, Payload(new SetupPayload { Recognition = new RecognitionSettings { CameraLocation = "DEU" } }));
        Assert.Equal("ALPV reads another sensor. Nothing was changed.", Assert.IsType<DeviceNotCompatibleException>(error).Message);
        Assert.Empty(other.AlpvWrites);

        var rotated = new AlpvCamera { ConfigXml = Fixture.Config317.Replace("<frame_rotate>0</frame_rotate>", "<frame_rotate>90</frame_rotate>", StringComparison.Ordinal) };
        var area = new AlpvArea { Id = 1, Corners = [new(0, 0), new(1000, 0), new(1000, 1000)] };
        (_, error) = await RunAsync(new SetupTask(), rotated, Payload(new SetupPayload { Areas = [area] }));
        Assert.IsType<DeviceNotCompatibleException>(error);
        Assert.Empty(rotated.AlpvWrites);
    }

    [Fact]
    public async Task Set_up_refuses_invalid_values_before_it_contacts_the_camera()
    {
        var camera = new AlpvCamera();

        var (_, error) = await RunAsync(new SetupTask(), camera, Payload(new SetupPayload { Recognition = new RecognitionSettings { ConfidenceThreshold = 2 } }));

        Assert.IsType<ArgumentException>(error);
        Assert.Empty(camera.Requests);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.19)]
    [InlineData(1.01)]
    public async Task Set_up_refuses_a_threshold_outside_ALPVs_0_2_to_1_before_the_first_write(double threshold)
    {
        var camera = new AlpvCamera();

        var (_, error) = await RunAsync(new SetupTask(), camera, Payload(new SetupPayload { Recognition = new RecognitionSettings { ConfidenceThreshold = threshold } }));

        Assert.Equal("The threshold is 0.2 to 1. " + LicensePlateTexts.NothingChanged, Assert.IsType<ArgumentException>(error).Message);
        Assert.Empty(camera.Requests);
        Assert.Empty(Tasks.SetupRules.Check(new SetupPayload { Recognition = new RecognitionSettings { ConfidenceThreshold = 0.2 } }));
        Assert.Empty(Tasks.SetupRules.Check(new SetupPayload { Recognition = new RecognitionSettings { ConfidenceThreshold = 1.0 } }));
    }

    [Fact]
    public async Task Set_up_saves_both_areas_and_turns_on_only_the_events_that_are_off()
    {
        var camera = new AlpvCamera { EventsJson = Fixture.Events.Replace("\"Lost\":\"true\"", "\"Lost\":\"false\"", StringComparison.Ordinal) };
        var area1 = new AlpvArea { Id = 1, Angle = 0, Corners = [new(100, 100), new(900, 100), new(900, 900), new(500, 950), new(100, 900)] };
        var area2 = new AlpvArea { Id = 2, Active = true, Angle = 180000, Corners = [new(10, 10), new(400, 10), new(400, 400)] };

        var (_, error) = await RunAsync(new SetupTask(), camera, Payload(new SetupPayload { Areas = [area1, area2] }));

        Assert.Null(error);
        Assert.Equal(["POST /local/fflprapp/config_json.cgi", "POST /local/fflprapp/vapix_events.cgi"], camera.AlpvWrites.Select(w => w.Request));
        Assert.Contains("\"active\":true", camera.AlpvWrites[0].Body!, StringComparison.Ordinal);
        Assert.Equal("""[{"Lost":"true"}]""", camera.AlpvWrites[1].Body);
        var areas = Alpv.AlpvClient.ParseAreas(camera.AreasJson);
        Assert.Equal(5, areas[0].Area.Corners.Count);
        Assert.True(areas[1].Area.Active);
    }

    [Fact]
    public async Task Set_up_without_changes_writes_nothing()
    {
        var camera = new AlpvCamera();
        var current = Alpv.AlpvConfig.Parse(camera.ConfigXml).Recognition;

        var (ctx, error) = await RunAsync(new SetupTask(), camera, Payload(new SetupPayload { Recognition = current }));

        Assert.Null(error);
        Assert.Empty(camera.AlpvWrites);
        Assert.Contains(ctx.Steps.Snapshot(), s => s.Name == SetupTask.RecognitionStep && s.State == TaskStepState.Skipped);
    }

    [Fact]
    public async Task Recommended_image_settings_log_the_five_parameters_before_and_after()
    {
        var camera = new AlpvCamera();
        var rechecked = new List<Guid>();

        var (ctx, error) = await RunAsync(new ImageSettingsTask(rechecked.Add), camera, "{}");

        Assert.Null(error);
        Assert.Equal(["GET /local/fflprapp/api.cgi?api=camera&sensor=setoptimalparams"], camera.AlpvWrites.Select(w => w.Request));
        Assert.Contains("Info: ImageSource.I0.Sensor.Contrast: 50 -> 65", ctx.LogLines);
        Assert.Contains("Info: ImageSource.I0.Sensor.ToneMapping: 20", ctx.LogLines);
        Assert.Equal(5, ctx.LogLines.Count(l => l.Contains("ImageSource.I0.Sensor.", StringComparison.Ordinal)));
        Assert.Equal([Device.Id], rechecked);
    }

    [Fact]
    public async Task Recommended_image_settings_write_nothing_when_ALPV_says_they_are_recommended()
    {
        var camera = new AlpvCamera { ConfigXml = Fixture.Config317.Replace("<isparamsoptimal>0</isparamsoptimal>", "<isparamsoptimal>1</isparamsoptimal>", StringComparison.Ordinal) };

        var (_, error) = await RunAsync(new ImageSettingsTask(), camera, "{}");

        Assert.Null(error);
        Assert.Empty(camera.AlpvWrites);
    }

    [Fact]
    public async Task Recommended_image_settings_refuse_ALPV_2()
    {
        var camera = new AlpvCamera { AppsXml = Fixture.AppsWith("2.13.4") };

        var (_, error) = await RunAsync(new ImageSettingsTask(), camera, "{}");

        Assert.IsType<DeviceNotCompatibleException>(error);
        Assert.Empty(camera.AlpvWrites);
    }

    [Fact]
    public void The_tasks_start_only_from_the_page()
    {
        ITaskPlugin[] tasks = [new SetupTask(), new ImageSettingsTask()];

        Assert.All(tasks, t => Assert.False(t.ShowInMenus));
        Assert.All(tasks, t => Assert.Empty(t.Permissions));
    }
}
