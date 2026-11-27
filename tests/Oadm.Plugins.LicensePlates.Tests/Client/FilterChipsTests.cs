using Oadm.Plugins.LicensePlates.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Plugins.LicensePlates.Tests.Client;

/// <summary>The Log's filters behind the Filter button (decision 2026-11-27): chips added, edited, removed and cleared; no Avalonia.</summary>
public sealed class FilterChipsTests
{
    private static FilterKind Kind(LogViewModel log, string name) => log.Filters.Kinds.Single(k => k.Name == name);

    [Fact]
    public async Task The_menu_lists_every_kind_and_no_chip_shows_without_a_filter()
    {
        var (page, _, _) = await Samples.OpenAsync();
        var filters = page.Log.Filters;

        Assert.Equal([PageTexts.Time, PageTexts.Result, PageTexts.Tags, PageTexts.Area, PageTexts.Direction, PageTexts.Country, PageTexts.Vehicle],
            filters.Kinds.Select(k => k.Name));
        Assert.False(filters.HasShown);
        Assert.False(filters.ClearCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_chosen_kind_shows_its_chip_and_opens_its_editor_and_goes_away_when_left_empty()
    {
        var (page, _, _) = await Samples.OpenAsync();
        var filters = page.Log.Filters;
        var country = Kind(page.Log, PageTexts.Country);
        FilterKind? opened = null;
        filters.OpenRequested += (_, kind) => opened = kind;

        filters.AddCommand.Execute(country);
        Assert.Same(country, opened);
        Assert.Equal([country], filters.Shown);
        Assert.True(filters.HasShown);

        filters.EditorClosed(country); // closed without a country
        Assert.False(filters.HasShown);
    }

    [Fact]
    public async Task A_choice_in_the_editor_filters_the_log_names_the_chip_and_closes_the_editor()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        var log = page.Log;
        var country = Kind(log, PageTexts.Country);
        var closed = false;
        country.CloseRequested += (_, _) => closed = true;
        log.Filters.AddCommand.Execute(country);

        var editor = Assert.IsType<ChoiceFilter>(country.Editor);
        editor.Selected = editor.Choices.OfType<CodeChoice>().Single(c => c.Value == "AUT");
        await log.LoadTask;

        Assert.True(closed);
        Assert.Equal("Country: Austria", country.Text);
        Assert.True(country.IsSet);
        Assert.Equal("AUT", ctx.LastPayload<LogPageRequest>(LicensePlatesMethods.Log).Query.Country);
        log.Filters.EditorClosed(country);
        Assert.Equal([country], log.Filters.Shown); // set: the chip stays
    }

    [Fact]
    public async Task The_x_of_a_chip_clears_its_filter_and_Clear_filters_clears_all_in_one_read()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        var log = page.Log;
        log.Range.Choose(DateRangePreset.Last24Hours);
        log.SelectedDirection = LogViewModel.DirectionChoices[1];
        log.SelectedCountry = log.CountryChoices.Single(c => c.Value == "AUT");
        await log.LoadTask;
        Assert.Equal(["Last 24 hours", "Direction: In", "Country: Austria"], log.Filters.Shown.Select(k => k.Text)); // set elsewhere: chips show

        log.Filters.RemoveCommand.Execute(Kind(log, PageTexts.Direction));
        await log.LoadTask;
        Assert.Null(log.Query().Direction);
        Assert.Equal(2, log.Filters.Shown.Count);

        var before = ctx.CallsOf(LicensePlatesMethods.Log);
        log.Filters.ClearCommand.Execute(null);
        await log.LoadTask;
        Assert.Equal(before + 1, ctx.CallsOf(LicensePlatesMethods.Log));
        Assert.False(log.Filters.HasShown);
        Assert.Null(log.Query().Country);
        Assert.True(log.Range.IsAll);
    }

    [Fact]
    public async Task The_vehicle_chip_reads_the_makes_when_its_editor_opens()
    {
        var (page, ctx, _) = await Samples.OpenAsync();
        var vehicle = Kind(page.Log, PageTexts.Vehicle);
        Assert.IsType<VehicleFilter>(vehicle.Editor);
        Assert.Equal(0, ctx.CallsOf(LicensePlatesMethods.VehicleMakes));

        page.Log.SelectedVehicleType = page.Log.VehicleTypeChoices.Single(c => c.Value == "CAR");
        await page.Log.LoadTask;
        Assert.Equal("Vehicle: Car", vehicle.Text);
        Assert.Contains(vehicle, page.Log.Filters.Shown);
    }
}
