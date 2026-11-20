using System.Text.Json.Nodes;

using Oadm.Plugins.Overlays.Client;

namespace Oadm.Plugins.Overlays.Tests;

/// <summary>The overlay API's answers as recorded on AXIS OS 10.12 and 12.11, and the requests OADM sends.</summary>
public sealed class ParserTests
{
    [Fact]
    public void The_api_list_names_the_overlay_api()
    {
        Assert.Contains(Fixtures.P3265ApiList(), a => a.Id == DynamicOverlayApi.ApiId && a.Version == "1.8");
        Assert.True(OverlaysTaskPlugin.CanRunOn(Fixtures.Camera()));
    }

    [Fact]
    public void Capabilities_of_both_axis_os_versions_and_of_the_documentation()
    {
        Assert.Equal(new OverlayCapabilities(512, 16, 200, 8, 1, 1), DynamicOverlayApi.ParseCapabilities(Fixtures.Data("getOverlayCapabilities-p3265v-12.11.json")));
        Assert.Equal(new OverlayCapabilities(512, 16, 48, 4, 1, 1), DynamicOverlayApi.ParseCapabilities(Fixtures.Data("getOverlayCapabilities-m3206lve-10.12.json")));
        // The documentation's example spells the slots "numberAvailbleSlots".
        Assert.Equal(new OverlayCapabilities(512, 16, 200, 16, 2, 1), DynamicOverlayApi.ParseCapabilities(Fixtures.Data("docs-getOverlayCapabilities-example.json")));
        Assert.Equal(new OverlayCapabilities(512, null, null, null, 1, 1), DynamicOverlayApi.ParseCapabilities(new JsonObject()));
    }

    [Fact]
    public void A_list_keeps_a_custom_place_an_empty_text_and_the_colours()
    {
        var list = DynamicOverlayApi.ParseList(Fixtures.Data("list-p3265v-12.11.json"));
        var only = Assert.Single(list.Texts);
        Assert.Equal(1, only.Identity);
        Assert.Equal(1, only.Camera);
        Assert.Equal(string.Empty, only.Overlay.Text);
        Assert.Equal(OverlayPosition.Custom, only.Overlay.Position);
        Assert.Equal([-0.25, -0.5], only.Overlay.CustomPosition);
        Assert.Equal((OverlayColor.White, OverlayColor.Transparent, OverlayColor.Black, 48), (only.Overlay.TextColor, only.Overlay.BackgroundColor, only.Overlay.OutlineColor, only.Overlay.FontSize));
        Assert.Empty(DynamicOverlayApi.ParseList(Fixtures.Data("list-empty-m3215lve-12.11.json")).Texts);
    }

    [Theory]
    [InlineData("list-two-m3215lve-12.11.json")]
    [InlineData("list-two-m3206lve-10.12.json")]
    public void A_real_line_break_comes_back_as_one_and_an_encoded_one_stays_text(string fixture)
    {
        var texts = DynamicOverlayApi.ParseList(Fixtures.Data(fixture)).Texts;
        Assert.Equal([1, 2], texts.Select(t => t.Identity));
        var first = texts[0].Overlay;
        Assert.EndsWith(" real\nbreak %F %X", first.Text, StringComparison.Ordinal);
        Assert.Equal((OverlayPosition.BottomLeft, OverlayColor.SemiTransparent, 32), (first.Position, first.BackgroundColor, first.FontSize));
        Assert.EndsWith(" encoded%0Abreak #n", texts[1].Overlay.Text, StringComparison.Ordinal);
        Assert.Equal([0.5, -0.5], texts[1].Overlay.CustomPosition);
    }

    [Theory]
    [InlineData("addText-limit-m3215lve-12.11.json", 300, "The camera has no free overlay slot.")]
    [InlineData("addText-fontsize-m3215lve-12.11.json", 309, "The camera does not accept this size.")]
    [InlineData("remove-identity-m3215lve-12.11.json", 302, "The overlay is no longer on the camera.")]
    [InlineData("list-without-params-m3215lve-12.11.json", 102, "The camera refused the request: Mandatory parameter(s) missing (error 102).")]
    public void Errors_in_an_answer_become_plain_messages(string fixture, int code, string message)
    {
        var error = Assert.Throws<OverlayDeviceException>(() => DynamicOverlayApi.ParseAnswer(Fixtures.Read(fixture)));
        Assert.Equal(code, error.Code);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void An_add_answer_gives_the_identity()
    {
        Assert.Equal(1, DynamicOverlayApi.ParseAnswer(Fixtures.Read("addText-m3215lve-12.11.json"))!["identity"]!.GetValue<int>());
        Assert.Throws<OverlayDeviceException>(() => DynamicOverlayApi.ParseAnswer("<html>"));
    }

    [Fact]
    public void AddText_sends_view_area_1_real_line_breaks_and_never_the_deprecated_parameters()
    {
        var p = DynamicOverlayApi.AddTextParams(new TextOverlay
        {
            Text = "Lobby\r\n%F %X",
            Position = OverlayPosition.TopRight,
            TextColor = OverlayColor.Red,
            BackgroundColor = OverlayColor.SemiTransparent,
            OutlineColor = OverlayColor.Black,
        });
        Assert.Equal("""{"camera":1,"text":"Lobby\n%F %X","position":"topRight","textColor":"red","textBGColor":"semiTransparent","textOLColor":"black"}""", p.ToJsonString());

        var custom = DynamicOverlayApi.AddTextParams(new TextOverlay { Text = "x", Position = OverlayPosition.Custom, CustomPosition = [-0.25, 0.5], FontSize = 32 });
        Assert.Equal("[-0.25,0.5]", custom["position"]!.ToJsonString());
        Assert.Equal(32, custom["fontSize"]!.GetValue<int>());
        Assert.False(custom.ContainsKey("reference"));
        Assert.False(custom.ContainsKey("scrollSpeed"));
        Assert.Contains("\"params\":{}", DynamicOverlayApi.Body("list", []), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_modifier_the_dialog_offers_is_one_the_camera_lists()
    {
        var recorded = JsonNode.Parse(Fixtures.Read("getOverlayModifiers-p3265v-12.11.json"))!["data"]!["groups"]!.AsObject()
            .SelectMany(g => g.Value!.AsArray()).Select(m => m!["mod"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var offered = OverlayModifiers.More.Append(OverlayModifiers.Date).Append(OverlayModifiers.Time).Select(m => m.Code)
            .Concat(OverlayModifiers.DynamicTexts.Select(m => m.Code[..2])); // #D takes an index
        Assert.All(offered, code => Assert.Contains(code, recorded));
        Assert.Equal(16, OverlayModifiers.DynamicTexts.Count);
        Assert.Equal("#D16", OverlayModifiers.DynamicTexts[^1].Code);
    }
}
