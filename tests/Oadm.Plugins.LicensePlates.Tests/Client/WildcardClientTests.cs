using Oadm.Plugins.LicensePlates.Client;

namespace Oadm.Plugins.LicensePlates.Tests.Client;

/// <summary>Wildcard plates on the page (decisions of 2026-11-27): the editor's rules, the search, the Log's tooltip and List entry.</summary>
public sealed class WildcardClientTests
{
    [Fact]
    public async Task The_editor_takes_a_wildcard_plate_and_says_when_it_has_too_few_fixed_characters()
    {
        var (page, _, _) = await Samples.OpenAsync();
        var editor = new PlateEditorViewModel(page, null, null, page.Plates.Exists);

        editor.Text = "axis 34**";
        Assert.Null(editor.ErrorOf(nameof(PlateEditorViewModel.Text)));
        editor.Text = "AB****";
        Assert.Equal(LicensePlateTexts.TooFewFixed, editor.ErrorOf(nameof(PlateEditorViewModel.Text)));
        editor.Text = "AXIS12**"; // in the list already
        Assert.Equal(LicensePlateTexts.PlateExists, editor.ErrorOf(nameof(PlateEditorViewModel.Text)));
        Assert.Equal("* stands for one letter or digit.", PageTexts.WildcardHint);
    }

    [Fact]
    public async Task The_plates_search_finds_a_wildcard_plate_by_its_fixed_part_and_by_a_plate_it_fits()
    {
        var (page, _, _) = await Samples.OpenAsync();
        page.Plates.SearchText = "AXIS";
        Assert.Equal(["AXIS12**"], page.Plates.Rows.Select(r => r.Text));
        page.Plates.SearchText = "AXIS 1234";
        Assert.Equal(["AXIS12**"], page.Plates.Rows.Select(r => r.Text));
        page.Plates.SearchText = "AXIS13";
        Assert.Empty(page.Plates.Rows);
    }

    [Fact]
    public async Task A_read_a_wildcard_plate_decided_shows_the_car_and_the_pattern()
    {
        var (page, _, _) = await Samples.OpenAsync();
        var row = page.Log.Rows.Single(r => r.PlateText == "AXIS1234");

        Assert.Equal("Matches AXIS12**", row.PlateTip);
        Assert.Equal("AXIS12**", row.ListEntryText);
        Assert.Equal("Company cars", row.Name);
        Assert.False(row.IsDeviated);
        Assert.Null(page.Log.Rows.Single(r => r.PlateText == "W99887Z").PlateTip);
        Assert.Contains(page.Log.Columns.Choosable, c => c.Key == LogColumns.ListEntry && !c.IsVisible); // optional, off at first
    }
}
