using System.Net;
using System.Text.Json;

using Oadm.Core.Tests.Hardware;
using Oadm.Core.Vapix;
using Oadm.Sdk.Testing;

using Xunit.Abstractions;

namespace Oadm.Plugins.Overlays.Tests;

/// <summary>
/// Opt-in write test of Text overlays on the dev cameras (dev-cameras.yaml), only with <c>OADM_OVERLAYS_WRITE=1</c>. It writes
/// only overlays of its own (the text starts with a marker of this run) and never touches a camera's own overlays: Add and Remove
/// matching run on every camera with the overlay API, Replace only on a camera whose view area 1 has no text overlay of its own.
/// Its overlays are removed at the end, also after a failure, and the camera's list must be as before.
/// </summary>
[Trait("Category", "HardwareWrite")]
public sealed class HardwareWriteTests(ITestOutputHelper output)
{
    public sealed class WriteFactAttribute : FactAttribute
    {
        public WriteFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("OADM_OVERLAYS_WRITE") != "1" || !DevCameras.Any)
            {
                Skip = "Set OADM_OVERLAYS_WRITE=1 and OADM_DEV_CAMERAS to write test overlays to the dev cameras.";
            }
        }
    }

    [WriteFact]
    public async Task Own_overlays_are_added_replaced_and_removed_and_the_cameras_own_stay()
    {
        var tested = 0;
        foreach (var camera in DevCameras.All)
        {
            using var handler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(camera.User, camera.Password),
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            using var vapix = new VapixClient(new Uri($"{camera.EffectiveScheme}://{camera.Address}/"), handler, timeout: TimeSpan.FromSeconds(20), disposeHandler: false);
            try
            {
                if (!OverlaysTaskPlugin.CanRunOn(Fixtures.Camera() with { Apis = await vapix.GetApiListAsync(CancellationToken.None) }))
                {
                    output.WriteLine($"{camera}: no overlay API, skipped");
                    continue;
                }
            }
#pragma warning disable CA1031 // a camera that does not answer is left out; the test needs one that does
            catch (Exception ex)
#pragma warning restore CA1031
            {
                output.WriteLine($"{camera}: not reachable ({ex.Message}), skipped");
                continue;
            }

            await RunOnAsync(camera.ToString(), vapix);
            tested++;
        }

        Assert.True(tested > 0, "No dev camera with the overlay API answered.");
    }

    private async Task RunOnAsync(string name, VapixClient vapix)
    {
        var ct = CancellationToken.None;
        var marker = "OADM test " + Guid.NewGuid().ToString("N")[..8];
        var before = await DynamicOverlayApi.ListAsync(vapix, ct);
        var own = before.TextsOf(DynamicOverlayApi.ViewArea).Count;
        output.WriteLine($"{name}: {before.Texts.Count} text overlays before ({own} in view area 1), marker {marker}");
        var a = new TextOverlay { Text = marker + " A %F %X", Position = OverlayPosition.BottomLeft };
        var b = new TextOverlay { Text = marker + " B\n#n", Position = OverlayPosition.TopRight, BackgroundColor = OverlayColor.SemiTransparent, FontSize = 32 };
        var c = new TextOverlay { Text = marker + " C", Position = OverlayPosition.Top, TextColor = OverlayColor.Red, BackgroundColor = OverlayColor.Transparent, OutlineColor = OverlayColor.Black };
        try
        {
            var add = await RunAsync(vapix, new OverlaysPayload { Overlays = [a, b] });
            Assert.All(StepRun.Lines(add.Steps), line => Assert.EndsWith(": Done", line, StringComparison.Ordinal));
            Assert.Equal(2, await CountAsync(vapix, marker));

            var again = await RunAsync(vapix, new OverlaysPayload { Overlays = [a, b] });
            Assert.Equal("2 already on the camera", StepRun.Detail(again.Steps, OverlaysTaskRunner.StepCheck));

            if (own == 0)
            {
                var replace = await RunAsync(vapix, new OverlaysPayload { Mode = OverlaysMode.Replace, Overlays = [b, c] });
                Assert.Equal("1 to remove, 1 to add, 1 already on the camera", StepRun.Detail(replace.Steps, OverlaysTaskRunner.StepCheck));
                var texts = (await DynamicOverlayApi.ListAsync(vapix, ct)).TextsOf(DynamicOverlayApi.ViewArea).Select(t => t.Overlay.Text).Order(StringComparer.Ordinal);
                Assert.Equal([b.Text, c.Text], texts);
                output.WriteLine($"{name}: Replace done");
            }
            else
            {
                output.WriteLine($"{name}: view area 1 has overlays of its own, Replace not run");
            }

            var remove = await RunAsync(vapix, new OverlaysPayload { Mode = OverlaysMode.RemoveMatching, Overlays = [a, b, c] });
            Assert.Equal(0, await CountAsync(vapix, marker));
            output.WriteLine($"{name}: {StepRun.Detail(remove.Steps, OverlaysTaskRunner.StepCheck)}");
        }
        finally
        {
            foreach (var left in (await DynamicOverlayApi.ListAsync(vapix, ct)).Texts.Where(t => t.Overlay.Text.StartsWith(marker, StringComparison.Ordinal)))
            {
                await DynamicOverlayApi.RemoveAsync(vapix, left.Identity, ct);
                output.WriteLine($"{name}: removed leftover {left.Identity}");
            }
        }

        var after = await DynamicOverlayApi.ListAsync(vapix, ct);
        Assert.Equal(Describe(before), Describe(after));
        output.WriteLine($"{name}: back to its {after.Texts.Count} text overlays");
    }

    private static string Describe(DeviceOverlays overlays) =>
        JsonSerializer.Serialize(overlays.Texts.Select(t => new { t.Identity, t.Camera, t.Overlay }), OverlaysJson.Options)
        + JsonSerializer.Serialize(overlays.ImagesPerCamera);

    private static async Task<int> CountAsync(VapixClient vapix, string marker) =>
        (await DynamicOverlayApi.ListAsync(vapix, CancellationToken.None)).Texts.Count(t => t.Overlay.Text.StartsWith(marker, StringComparison.Ordinal));

    private static async Task<FakeTaskContext> RunAsync(VapixClient vapix, OverlaysPayload payload)
    {
        var ctx = new FakeTaskContext(vapix);
        await StepRun.RunAsync(ctx.Steps, () => TaskPluginTests.For(payload.Mode).ExecuteAsync(ctx, Fixtures.Camera(), OverlaysJson.Serialize(new OverlayListPayload { Overlays = payload.Overlays }), CancellationToken.None));
        return ctx;
    }
}
