using System.Text.Json.Nodes;
using System.Xml.Linq;

using Oadm.Plugins.Acap;
using Oadm.Plugins.LicensePlates.Alpv;
using Oadm.Plugins.LicensePlates.Tasks;

namespace Oadm.Plugins.LicensePlates.Tests;

/// <summary>ALPV's answers from the recorded fixtures of 3.0.13 and 3.1.7: parsing (secrets dropped) and what a write sends.</summary>
public sealed class AlpvConfigTests
{
    [Theory]
    [InlineData("alpv-3.1.7-config.xml")]
    [InlineData("alpv-3.0.13-config.xml")]
    public void The_secrets_are_dropped_while_the_answer_is_parsed(string fixture)
    {
        var config = AlpvConfig.Parse(Fixture.Text(fixture));

        Assert.DoesNotContain(config.Values, v => v.Value.Contains("fixture-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(config.Values.Keys, k => AlpvConfig.Secrets.Contains(k.Split('.')[^1]));
    }

    [Fact]
    public void The_recognition_fields_of_317()
    {
        var config = AlpvConfig.Parse(Fixture.Config317);

        Assert.Equal(new RecognitionSettings
        {
            Region = "5", // lprUseCase: Europe (regional_settings is the unit system, "metric")
            CameraLocation = "AUT",
            State = string.Empty,
            PlateWidthMin = 50,
            PlateWidthMax = 300,
            CharactersMin = 4,
            CharactersMax = 10,
            ConfidenceThreshold = 0.25,
        }, config.Recognition);
        Assert.True(config.ReadsFirstSensor);
        Assert.False(config.Rotated);
        Assert.True(config.Has("night_mode.sensitivity"));
    }

    [Fact]
    public void A_field_317_has_and_3013_has_not_is_never_sent_to_3013()
    {
        var old = AlpvConfig.Parse(Fixture.Config3013);
        var current = AlpvConfig.Parse(Fixture.Config317);
        Assert.False(old.Has("night_mode.sensitivity"));
        Assert.False(old.Has("plate_min_size_symb"));
        Assert.True(current.Has("plate_min_size_symb"));

        var wanted = new RecognitionSettings { CharactersMin = 5, ConfidenceThreshold = 0.3 };
        Assert.Equal(["plate.plate_min_size_symb", "plate.threshold"], SetupRules.Changes(old, wanted).Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["plate.plate_min_size_symb", "plate.threshold", "plate_min_size_symb"], SetupRules.Changes(current, wanted).Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Only_changed_values_are_written()
    {
        var config = AlpvConfig.Parse(Fixture.Config317);
        var same = config.Recognition;

        Assert.Empty(SetupRules.Changes(config, same));
        Assert.Null(AlpvClient.ConfigBody(SetupRules.Changes(config, same)));

        var changes = SetupRules.Changes(config, same with { CameraLocation = "DEU", PlateWidthMax = 280 });
        var body = AlpvClient.ConfigBody(changes)!;
        var root = XElement.Parse(body[body.IndexOf("<configuration", StringComparison.Ordinal)..]);
        Assert.Equal("DEU", root.Element("CountryPriority")!.Value);
        Assert.Equal("280", root.Element("plate")!.Element("plate_max_size")!.Value);
        Assert.Equal(2, root.Descendants().Count(e => !e.HasElements));
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_image_settings_compare_ALPVs_own_values()
    {
        var image = AlpvConfig.Parse(Fixture.Config317).Image!;

        Assert.False(image.Recommended);
        Assert.Contains(image.Values, v => v is { Name: "Contrast", Current: "49", Recommended: "65" });
        Assert.Contains(image.Values, v => v is { Name: "Shutter speed", Current: "1/100", Recommended: "1/500" });
        Assert.Contains(image.Values, v => v is { Name: "WDR", Matches: true });
    }

    [Fact]
    public void Another_video_channel_is_another_sensor()
    {
        var config = AlpvConfig.Parse(Fixture.Config317.Replace("<videoChannelId>1</videoChannelId>", "<videoChannelId>2</videoChannelId>", StringComparison.Ordinal));

        Assert.False(config.ReadsFirstSensor);
    }

    [Fact]
    public void The_areas_are_read_and_both_are_written_back_with_their_other_keys()
    {
        var areas = AlpvClient.ParseAreas(Fixture.Areas);
        Assert.Equal(2, areas.Count);
        Assert.True(areas[0].Area.Active);
        Assert.False(areas[1].Area.Active);
        Assert.Equal(180000, areas[0].Area.Angle);
        Assert.Equal([new AreaPoint(50, 50), new AreaPoint(950, 50), new AreaPoint(950, 950), new AreaPoint(50, 950)], areas[0].Area.Corners);

        var five = new AlpvArea { Id = 1, Angle = 90000, Corners = [new(10, 10), new(500, 10), new(900, 500), new(500, 900), new(10, 500)] };
        var body = JsonNode.Parse(AlpvClient.AreasBody(areas, [five]))!;
        Assert.Equal("setconfig", body["api"]!.GetValue<string>());
        Assert.Equal("roi", body["config"]!.GetValue<string>());
        var roi = body["roi"]!.AsArray();
        Assert.Equal(2, roi.Count);
        Assert.Equal(90000, roi[0]!["angle"]!.GetValue<int>());
        Assert.Equal(5, roi[0]!["coordinates"]!.AsArray().Count);
        Assert.Equal("polygon", roi[0]!["type"]!.GetValue<string>());
        Assert.False(roi[1]!["active"]!.GetValue<bool>());
        Assert.Equal(4, roi[1]!["coordinates"]!.AsArray().Count); // area 2 keeps its corners
    }

    [Fact]
    public void The_event_switches_turn_on_only_what_is_off()
    {
        var on = AlpvClient.ParseEvents(Fixture.Events);
        Assert.True(AlpvClient.SwitchesOf(on).AllOn);
        Assert.Null(AlpvClient.EventsBody(on));

        var off = AlpvClient.ParseEvents(Fixture.Events.Replace("\"Update\":\"true\"", "\"Update\":\"false\"", StringComparison.Ordinal).Replace("\"Roi1\":\"true\"", "\"Roi1\":\"false\"", StringComparison.Ordinal));
        Assert.False(AlpvClient.SwitchesOf(off).AllOn);
        Assert.Equal("""[{"Update":"true"}]""", AlpvClient.EventsBody(off));
    }

    [Theory]
    [InlineData("3.1.7", "Running", "Valid", null)]
    [InlineData("3.0.13", "Running", "Valid", null)]
    [InlineData("2.13.4", "Running", "Valid", CameraStatus.AlpvNeedsVersion3)]
    [InlineData("4.0.1", "Running", "Valid", CameraStatus.AlpvNeedsVersion3)]
    [InlineData("3.1.7", "Stopped", "Valid", CameraStatus.AlpvStopped)]
    [InlineData("3.1.7", "Running", "Missing", CameraStatus.AlpvNoLicense)]
    [InlineData(null, "Running", "Valid", CameraStatus.AlpvNotInstalled)]
    public void ALPV_3_licensed_and_running_takes_part(string? version, string status, string license, CameraStatus? problem)
    {
        var app = AlpvClient.Find(ApplicationApiClient.ParseList(Fixture.AppsWith(version, status, license)));

        Assert.Equal(problem, AlpvClient.Problem(app));
    }

    [Fact]
    public void The_recorded_application_list_has_ALPV_317()
    {
        var app = AlpvClient.Find(ApplicationApiClient.ParseList(Fixture.Apps))!;

        Assert.Equal("3.1.7", app.Version);
        Assert.Null(AlpvClient.Problem(app));
    }

    [Theory]
    [InlineData(0, 1000, true)]
    [InlineData(-1, 0, false)]
    [InlineData(0, 1001, false)]
    public void Corners_stay_inside_the_picture(int x, int y, bool valid)
    {
        var payload = new SetupPayload { Areas = [new AlpvArea { Id = 1, Corners = [new(x, y), new(10, 10), new(20, 20)] }] };

        Assert.Equal(valid, SetupRules.Check(payload).Count == 0);
    }

    [Fact]
    public void An_area_has_3_to_8_corners_and_area_1_is_needed()
    {
        static AlpvArea Area(int id, int corners) => new() { Id = id, Corners = [.. Enumerable.Range(0, corners).Select(i => new AreaPoint(i * 10, i * 5))] };

        Assert.Contains("areas.1", SetupRules.Check(new SetupPayload { Areas = [Area(1, 2)] }).Keys);
        Assert.Contains("areas.1", SetupRules.Check(new SetupPayload { Areas = [Area(1, 9)] }).Keys);
        Assert.Contains("areas", SetupRules.Check(new SetupPayload { Areas = [Area(2, 4)] }).Keys);
        Assert.Empty(SetupRules.Check(new SetupPayload { Areas = [Area(1, 3), Area(2, 8)] }));
    }

    [Fact]
    public void Recognition_values_are_checked_before_anything_is_sent()
    {
        var errors = SetupRules.Check(new SetupPayload
        {
            Recognition = new RecognitionSettings { PlateWidthMin = 300, PlateWidthMax = 50, ConfidenceThreshold = 1.5, CameraLocation = "at" },
        });

        Assert.Equal(["recognition.cameraLocation", "recognition.confidenceThreshold", "recognition.plateWidthMax"], errors.Keys.Order(StringComparer.Ordinal));
    }
}
