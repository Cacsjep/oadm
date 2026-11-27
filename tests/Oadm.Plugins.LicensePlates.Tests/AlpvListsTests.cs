using Oadm.Plugins.LicensePlates.Alpv;
using Oadm.Plugins.LicensePlates.Tasks;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.LicensePlates.Tests;

/// <summary>
/// ALPV's lists shipped with the server part (read from ALPV 3.1.7's page): regions, the countries of a region, the states,
/// the vehicle types and colours; the Set up task writes the region to <c>lprUseCase</c>; the make and model lists of the camera.
/// </summary>
public sealed class AlpvListsTests
{
    private static AlpvLists Lists => AlpvListData.Lists;

    [Fact]
    public void The_lists_of_317_are_shipped()
    {
        Assert.Equal("3.1.7", Lists.Version);
        Assert.Equal(17, Lists.Regions.Count);
        Assert.Equal(new AlpvRegion(5, "Europe"), Lists.Regions[0]);
        Assert.Contains(new AlpvRegion(16, "USA"), Lists.Regions);
        Assert.Equal([16, 17, 19, 21], Lists.RegionsWithStates);
        Assert.Equal(32, Lists.States.Count);
        Assert.All(Lists.States, s => Assert.Equal("MX", s.Country)); // the file holds the states of Mexico only
        Assert.Equal("Austria", Lists.Country("AUT")!.Name);
        Assert.Equal("United States of America", Lists.Country("US")!.Name);
    }

    [Fact]
    public void Europe_offers_the_EU_and_CIS_countries_and_no_state()
    {
        var europe = Lists.CountriesOf(5);
        Assert.Contains(europe, c => c.Code == "AUT");
        Assert.Contains(europe, c => c.Code == "ARM"); // CIS
        Assert.DoesNotContain(europe, c => c.Code == "US");
        Assert.Equal(europe.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(c => c.Code), europe.Select(c => c.Code));
        Assert.Empty(Lists.StatesOf(5, "AUT"));

        Assert.Contains(Lists.CountriesOf(4), c => c.Code == "AUT"); // EU only
        Assert.DoesNotContain(Lists.CountriesOf(4), c => c.Code == "ARM");
    }

    [Fact]
    public void Regions_with_states_offer_the_states_of_the_chosen_country()
    {
        Assert.Contains(Lists.CountriesOf(16), c => c.Code == "US");
        Assert.Contains(Lists.CountriesOf(21), c => c.Code == "MX");
        Assert.Contains(Lists.StatesOf(19, "MX"), s => s.Code == "CMX" && s.Name == "Ciudad de México");
        Assert.Equal(32, Lists.StatesOf(21, "MX").Count);
        Assert.Empty(Lists.StatesOf(16, "US")); // a region with states, but the list has none for the country
        Assert.Empty(Lists.StatesOf(5, "MX")); // a region without states
    }

    [Fact]
    public void Vehicle_types_and_colours_keep_ALPVs_value_and_show_OADMs_wording()
    {
        Assert.Contains(new AlpvChoice("CAR", "Car"), Lists.VehicleTypes);
        Assert.Contains(new AlpvChoice("SUV", "SUV"), Lists.VehicleTypes);
        Assert.Contains(new AlpvChoice("UNDEFINED", "Unknown"), Lists.VehicleTypes);
        Assert.Contains(new AlpvChoice("Silver", "Silver"), Lists.VehicleColors);
        Assert.Equal(11, Lists.VehicleColors.Count);
    }

    [Fact]
    public void The_lists_travel_through_the_contract()
    {
        var json = PluginJson.Serialize(Lists, LicensePlatesJson.Options);
        var back = PluginJson.Parse<AlpvLists>(json, LicensePlatesJson.Options);
        Assert.Equal(Lists.Countries.Count, back.Countries.Count);
        Assert.Equal(Lists.CountriesOf(5).Count, back.CountriesOf(5).Count);
        Assert.Equal(Lists.StatesOf(19, "MX").Count, back.StatesOf(19, "MX").Count);
    }

    [Fact]
    public void Region_location_and_state_are_checked_as_codes_of_the_lists()
    {
        Assert.Empty(SetupRules.Check(new SetupPayload { Recognition = new RecognitionSettings { Region = "16", CameraLocation = "US", State = string.Empty } }));
        Assert.Empty(SetupRules.Check(new SetupPayload { Recognition = new RecognitionSettings { CameraLocation = AlpvLists.NoCountry } }));
        var errors = SetupRules.Check(new SetupPayload { Recognition = new RecognitionSettings { Region = "metric", CameraLocation = "Austria", State = "C M X" } });
        Assert.Equal(["recognition.cameraLocation", "recognition.region", "recognition.state"], errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_new_region_is_written_to_lprUseCase_never_to_regional_settings()
    {
        var config = AlpvConfig.Parse(Fixture.Config317);
        var changes = SetupRules.Changes(config, new RecognitionSettings { Region = "21", CameraLocation = "MX", State = "CMX" });

        Assert.Equal(["CountryPriority", "StatePriority", "lprUseCase"], changes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("21", changes["lprUseCase"]);
        var body = AlpvClient.ConfigBody(changes)!;
        Assert.Contains("<lprUseCase>21</lprUseCase>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("regional_settings", body, StringComparison.Ordinal);
        Assert.Empty(SetupRules.Changes(config, new RecognitionSettings { Region = "5", CameraLocation = "AUT", State = string.Empty }));
    }

    [Fact]
    public void The_make_and_model_lists_of_the_camera_are_read_without_the_empty_entry()
    {
        var makes = AlpvClient.ParseNames(Fixture.Text("alpv-listbrands.json"), "brands");
        Assert.Equal("(None)", makes[0]);
        Assert.Contains("Volkswagen", makes);
        Assert.DoesNotContain(string.Empty, makes);

        var models = AlpvClient.ParseNames(Fixture.Text("alpv-listmodels-volkswagen.json"), "models");
        Assert.Contains("Golf", models);
        Assert.Contains("Arteon/Arteon R", models); // the escaped slash of the answer
        Assert.Throws<AlpvException>(() => AlpvClient.ParseNames("{}", "brands"));
    }
}
