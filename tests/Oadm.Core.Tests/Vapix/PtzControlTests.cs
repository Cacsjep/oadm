using System.Net;

using Oadm.Core.LiveView;
using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.Vapix;

/// <summary>PTZ of the live view against answers recorded on an AXIS Q6411-LE (AXIS OS 13.0.88, AXIS Virtual Loan Tool).</summary>
public sealed class PtzControlTests
{
    private static readonly Uri Base = new("http://camera.example:12090/");

    [Fact]
    public async Task APtzCameraListsItsCommandsPresetsAndZoom()
    {
        using var client = new VapixClient(Base, Camera(ptz: true, out _));
        var state = await client.GetPtzAsync(1, CancellationToken.None);

        Assert.True(state.IsSupported);
        Assert.True(state.CanMove && state.CanZoom && state.CanCenter && state.CanGoHome);
        Assert.Equal(["Home", "Gate"], state.Presets);
        Assert.Equal(1500, state.Zoom);
        Assert.Equal(["Home"], (await client.GetPtzAsync(2, CancellationToken.None)).Presets); // the thermal channel
    }

    [Fact]
    public async Task ACameraWithoutPtzIsNotSupportedWithoutAnError()
    {
        using var client = new VapixClient(Base, Camera(ptz: false, out var paths));
        Assert.Same(PtzState.None, await client.GetPtzAsync(1, CancellationToken.None));
        Assert.DoesNotContain(paths, p => p.Contains("ptz.cgi", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PtzCommandKind.Move, 40.4, -150, 0, null, "continuouspantiltmove=40,-100&timeout=1")]
    [InlineData(PtzCommandKind.Move, 0.2, -0.3, 0, null, "continuouspantiltmove=0,0")]
    [InlineData(PtzCommandKind.Stop, 0, 0, 0, null, "continuouspantiltmove=0,0")]
    [InlineData(PtzCommandKind.Zoom, 0, 0, 20000, null, "zoom=9999")]
    [InlineData(PtzCommandKind.ZoomBy, 0, 0, -1200, null, "rzoom=-1200")]
    [InlineData(PtzCommandKind.Center, 0.75, 1.4, 0, null, "center=750,1000&imagewidth=1000&imageheight=1000")]
    [InlineData(PtzCommandKind.Home, 0, 0, 0, null, "move=home")]
    [InlineData(PtzCommandKind.Preset, 0, 0, 0, "Gate & Door", "gotoserverpresetname=Gate%20%26%20Door")]
    public void CommandsAreClampedAndEscaped(PtzCommandKind kind, double x, double y, int value, string? preset, string expected) =>
        Assert.Equal(expected, new PtzCommand(kind, x, y, value, preset).Arguments());

    [Fact]
    public async Task TheCamerasErrorAnswerIsAPtzExceptionWithItsText()
    {
        Assert.Throws<ArgumentException>(() => new PtzCommand(PtzCommandKind.Preset, Preset: "a\nb").Arguments());
        using var client = new VapixClient(Base, new FakeHttpMessageHandler((_, _) => Fixtures.Text(Fixtures.Read("ptz-error-preset.txt"))));
        var ex = await Assert.ThrowsAsync<PtzException>(() => client.SendPtzAsync(1, "gotoserverpresetname=Nope", CancellationToken.None));
        Assert.Equal("goto preset: No such preset position found", ex.Message);
    }

    private static FakeHttpMessageHandler Camera(bool ptz, out List<string> paths)
    {
        var seen = new List<string>();
        paths = seen;
        return new FakeHttpMessageHandler((request, _) =>
        {
            var query = request.RequestUri!.PathAndQuery;
            seen.Add(query);
            if (query.Contains("param.cgi", StringComparison.Ordinal))
            {
                return Fixtures.Text($"root.Properties.PTZ.PTZ={(ptz ? "yes" : "no")}\n");
            }

            return query switch
            {
                _ when query.Contains("info=1", StringComparison.Ordinal) => Fixtures.Text(Fixtures.Read("ptz-info-Q6411-LE.txt")),
                _ when query.Contains("presetposall", StringComparison.Ordinal) => Fixtures.Text(Fixtures.Read("ptz-presetposall-Q6411-LE.json")),
                _ when query.Contains("query=position", StringComparison.Ordinal) => Fixtures.Text(Fixtures.Read("ptz-position-Q6411-LE.txt")),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            };
        });
    }
}
