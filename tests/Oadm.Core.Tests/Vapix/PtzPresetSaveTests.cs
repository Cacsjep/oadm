using System.Net;

using Oadm.Core.LiveView;
using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.Vapix;

/// <summary>
/// Saving the current position to a preset (<c>setserverpresetname</c>): only a name the camera lists, the home preset with
/// <c>home=yes</c>. The presets are the AXIS Q6411-LE's recorded answer; the <c>PTZ.Preset</c> parameters are not recorded
/// from a camera (unverified).
/// </summary>
public sealed class PtzPresetSaveTests
{
    private static readonly Uri Base = new("http://camera.example:12090/");

    private const string PresetParameters =
        "root.PTZ.Preset.P0.HomePosition=1\nroot.PTZ.Preset.P0.ImageSource=0\nroot.PTZ.Preset.P0.Position.P1.Name=Home\n"
        + "root.PTZ.Preset.P0.Position.P2.Name=Gate\nroot.PTZ.Preset.P1.HomePosition=-1\nroot.PTZ.Preset.P1.ImageSource=1\n";

    [Theory]
    [InlineData("Gate", false, "setserverpresetname=Gate")]
    [InlineData("Home", true, "setserverpresetname=Home&home=yes")]
    [InlineData("Gate & Door", false, "setserverpresetname=Gate%20%26%20Door")]
    public void TheSaveNamesThePresetEscapedAndKeepsHomeOnlyForTheHomePreset(string preset, bool home, string expected) =>
        Assert.Equal(expected, new PtzCommand(PtzCommandKind.SavePreset, Preset: preset, Home: home).Arguments());

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("a\nb")]
    public void ASaveWithoutAUsableNameIsRefused(string? preset) =>
        Assert.Throws<ArgumentException>(() => new PtzCommand(PtzCommandKind.SavePreset, Preset: preset).Arguments());

    [Fact]
    public async Task APresetTheCameraListsIsSavedAndTheHomePresetStaysHome()
    {
        using var client = new VapixClient(Base, Camera(out var queries));
        await DevicePtz.SavePresetAsync(client, 1, new PtzCommand(PtzCommandKind.SavePreset, Preset: "Gate"), CancellationToken.None);
        await DevicePtz.SavePresetAsync(client, 1, new PtzCommand(PtzCommandKind.SavePreset, Preset: "Home"), CancellationToken.None);
        await DevicePtz.SavePresetAsync(client, 2, new PtzCommand(PtzCommandKind.SavePreset, Preset: "Home"), CancellationToken.None);

        Assert.Equal(
            ["camera=1&setserverpresetname=Gate", "camera=1&setserverpresetname=Home&home=yes", "camera=2&setserverpresetname=Home"],
            queries.Where(q => q.Contains("setserverpresetname", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(1, "Nope")]
    [InlineData(1, "gate")] // another spelling would add a new preset
    [InlineData(2, "Gate")] // the thermal channel lists only Home
    public async Task APresetTheCameraDoesNotListIsRefusedAndNothingIsSent(int camera, string preset)
    {
        using var client = new VapixClient(Base, Camera(out var queries));
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            DevicePtz.SavePresetAsync(client, camera, new PtzCommand(PtzCommandKind.SavePreset, Preset: preset), CancellationToken.None));
        Assert.StartsWith(PtzCommand.NoSuchPreset, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(queries, q => q.Contains("setserverpresetname", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThePresetsComeWithTheirNumbersAndTheHomeOfTheChannel()
    {
        using var client = new VapixClient(Base, Camera(out _));
        var presets = await client.GetPtzPresetsAsync(1, CancellationToken.None);
        Assert.Equal([new PtzPreset(1, "Home"), new PtzPreset(2, "Gate")], presets.Presets);
        Assert.Equal(1, presets.HomeNumber);
        Assert.Null((await client.GetPtzPresetsAsync(2, CancellationToken.None)).HomeNumber);
    }

    [Theory]
    [InlineData("root.PTZ.Preset.P0.HomePosition=3\n", 1, 3)] // no ImageSource: the group of the channel
    [InlineData("root.PTZ.Preset.P0.HomePosition=3\n", 2, null)]
    [InlineData("root.PTZ.Preset.P0.HomePosition=-1\n", 1, null)]
    [InlineData("root.PTZ.Preset.P0.ImageSource=1\nroot.PTZ.Preset.P0.HomePosition=2\nroot.PTZ.Preset.P1.ImageSource=0\nroot.PTZ.Preset.P1.HomePosition=5\n", 1, 5)]
    [InlineData("root.PTZ.Preset.P0.ImageSource=1\nroot.PTZ.Preset.P0.HomePosition=2\n", 1, null)]
    [InlineData("", 1, null)]
    public void TheHomePresetIsTheHomePositionOfTheChannelsGroup(string parameters, int camera, int? expected) =>
        Assert.Equal(expected, VapixParsers.PtzHomePreset(VapixParsers.ParseParameterList(parameters), camera));

    private static FakeHttpMessageHandler Camera(out List<string> queries)
    {
        var seen = new List<string>();
        queries = seen;
        return new FakeHttpMessageHandler((request, _) =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query.TrimStart('?'));
            lock (seen)
            {
                seen.Add(query);
            }

            return query switch
            {
                _ when request.RequestUri.AbsolutePath.Contains("param.cgi", StringComparison.Ordinal) => Fixtures.Text(PresetParameters),
                _ when query.Contains("presetposall", StringComparison.Ordinal) => Fixtures.Text(Fixtures.Read("ptz-presetposall-Q6411-LE.json")),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            };
        });
    }
}
