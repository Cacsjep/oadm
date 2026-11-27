using Oadm.Plugins.LicensePlates.Client;

namespace Oadm.Plugins.LicensePlates.Tests.Client;

/// <summary>
/// The selects fed by ALPV's lists: Region, Camera location and State of Set up (filtered by region, the camera's unknown
/// values kept), and the Log's Country, vehicle type, colour, make and model filters; no Avalonia.
/// </summary>
public sealed class AlpvListsClientTests
{
    private static async Task<(SetupViewModel Setup, FakePageContext Ctx)> SetupAsync(Func<SetupRead, SetupRead>? change = null)
    {
        var ctx = Samples.Context();
        if (change is not null)
        {
            ctx.Answers[LicensePlatesMethods.ReadSetup] = _ => Samples.Serialize(change(Samples.Setup()));
        }

        var (page, _, _) = await Samples.OpenAsync(ctx);
        var setup = new SetupViewModel(page, page.Cameras.Rows[0]);
        await setup.LoadAsync();
        return (setup, ctx);
    }

    [Fact]
    public async Task Europe_offers_the_EU_and_CIS_countries_and_no_state()
    {
        var (setup, _) = await SetupAsync();
        using var _ = setup;

        Assert.Equal("Europe", setup.SelectedRegion!.Name);
        Assert.Equal("Austria", setup.SelectedLocation!.Name);
        Assert.Equal(PageTexts.None, setup.LocationChoices[0].Name); // ALPV's "Select": value "0"
        Assert.Equal(AlpvLists.NoCountry, setup.LocationChoices[0].Value);
        Assert.Contains(setup.LocationChoices, c => c.Value == "ARM"); // CIS
        Assert.DoesNotContain(setup.LocationChoices, c => c.Value == "US");
        Assert.False(setup.ShowState);
        Assert.Null(setup.ToRequest().Setup.Recognition); // nothing changed
    }

    [Fact]
    public async Task A_region_with_states_offers_the_states_of_the_chosen_country_and_clears_what_it_does_not_offer()
    {
        var (setup, _) = await SetupAsync();
        using var _ = setup;

        setup.SelectedRegion = setup.RegionChoices.Single(r => r.Name == "Mexico");
        Assert.Equal(AlpvLists.NoCountry, setup.CameraLocation); // Austria is not offered for Mexico
        Assert.False(setup.ShowState);

        setup.SelectedLocation = setup.LocationChoices.Single(c => c.Value == "MX");
        Assert.True(setup.ShowState);
        Assert.Equal(PageTexts.None, setup.StateChoices[0].Name);
        setup.SelectedState = setup.StateChoices.Single(s => s.Value == "CMX");

        var recognition = setup.ToRequest().Setup.Recognition!;
        Assert.Equal("19", recognition.Region);
        Assert.Equal("MX", recognition.CameraLocation);
        Assert.Equal("CMX", recognition.State);

        // USA: a region with states, the United States offered; the list holds no US states, so no State select.
        setup.SelectedRegion = setup.RegionChoices.Single(r => r.Name == "USA");
        Assert.Equal(AlpvLists.NoCountry, setup.CameraLocation);
        Assert.Equal(string.Empty, setup.State);
        setup.SelectedLocation = setup.LocationChoices.Single(c => c.Value == "US");
        Assert.False(setup.ShowState);
    }

    [Fact]
    public async Task Values_the_list_does_not_know_stay_as_their_code_and_are_never_changed_unless_picked()
    {
        var (setup, _) = await SetupAsync(read => read with
        {
            Recognition = read.Recognition with { Region = "999", CameraLocation = "XYZ", State = "QQ" },
        });
        using var _ = setup;

        Assert.Equal(new CodeChoice("999", "999"), setup.SelectedRegion);
        Assert.Equal(new CodeChoice("XYZ", "XYZ"), setup.SelectedLocation);
        Assert.Null(setup.ToRequest().Setup.Recognition);

        // A known country outside the region shows by its name until the region changes.
        var (other, _) = await SetupAsync(read => read with { Recognition = read.Recognition with { CameraLocation = "US" } });
        using var __ = other;
        Assert.Equal("United States of America", other.SelectedLocation!.Name);
        Assert.Null(other.ToRequest().Setup.Recognition);
    }

    [Fact]
    public async Task A_camera_without_the_fields_never_gets_them()
    {
        var (setup, _) = await SetupAsync(read => read with
        {
            Recognition = read.Recognition with { Region = null, CameraLocation = null, State = null },
        });
        using var _ = setup;

        Assert.False(setup.HasRegion);
        Assert.False(setup.HasCameraLocation);
        Assert.False(setup.ShowState);
        Assert.Null(setup.ToRequest().Setup.Recognition);
    }

    [Fact]
    public async Task The_log_filters_by_country_vehicle_type_and_colour_from_the_lists()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        var log = page.Log;

        Assert.Equal(PageTexts.AllCountries, log.CountryChoices[0].Name);
        Assert.Contains(log.CountryChoices, c => c.Value == "AUT" && c.Name == "Austria");
        Assert.Equal(log.CountryChoices.Skip(1).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase), log.CountryChoices.Skip(1));
        Assert.Contains(log.VehicleTypeChoices, c => c.Value == "CAR" && c.Name == "Car");
        Assert.Contains(log.VehicleColorChoices, c => c.Value == "Gray");

        log.SelectedCountry = log.CountryChoices.Single(c => c.Value == "AUT");
        log.SelectedVehicleType = log.VehicleTypeChoices.Single(c => c.Value == "SUV");
        log.SelectedVehicleColor = log.VehicleColorChoices.Single(c => c.Value == "Black");
        await log.LoadTask;

        var query = ctx.LastPayload<LogPageRequest>(LicensePlatesMethods.Log).Query;
        Assert.Equal("AUT", query.Country);
        Assert.Equal("SUV", query.VehicleType);
        Assert.Equal("Black", query.VehicleColor);
        Assert.Equal("Vehicle: SUV, Black", log.VehicleText);
        Assert.True(log.HasFilters);

        log.ClearFiltersCommand.Execute(null);
        await log.LoadTask;
        Assert.Null(log.Query().Country);
        Assert.Null(log.Query().VehicleType);
    }

    [Fact]
    public async Task Makes_are_read_when_the_vehicle_filter_opens_and_models_after_a_make()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        var log = page.Log;
        Assert.Equal(0, ctx.CallsOf(LicensePlatesMethods.VehicleMakes)); // never before the flyout opens
        Assert.False(log.CanChooseModel);

        log.EnsureMakes();
        await log.NamesTask;
        log.EnsureMakes(); // once
        Assert.Equal(1, ctx.CallsOf(LicensePlatesMethods.VehicleMakes));
        Assert.Equal([PageTexts.Any, "(None)", "Audi", "BMW", "Skoda", "Volkswagen"], log.MakeChoices.Select(c => c.Name));

        log.SelectedMake = log.MakeChoices.Single(c => c.Value == "Volkswagen");
        await log.NamesTask;
        Assert.True(log.CanChooseModel);
        Assert.Equal("Volkswagen", ctx.LastPayload<VehicleNamesRequest>(LicensePlatesMethods.VehicleModels).Make);
        log.SelectedModel = log.ModelChoices.Single(c => c.Value == "Golf");
        await log.LoadTask;
        var query = ctx.LastPayload<LogPageRequest>(LicensePlatesMethods.Log).Query;
        Assert.Equal("Volkswagen", query.VehicleMake);
        Assert.Equal("Golf", query.VehicleModel);
    }

    [Fact]
    public async Task Without_a_connected_camera_the_make_says_why_below_it()
    {
        var ctx = Samples.Context();
        ctx.Answers[LicensePlatesMethods.VehicleMakes] = _ => Samples.Serialize(new VehicleNamesReply([], LicensePlateTexts.NoCameraConnected));
        var (page, _, _) = await Samples.OpenAsync(ctx);

        page.Log.EnsureMakes();
        await page.Log.NamesTask;
        Assert.Equal(LicensePlateTexts.NoCameraConnected, page.Log.NamesProblem);
        Assert.Single(page.Log.MakeChoices);
    }
}
