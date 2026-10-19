using System.Net;
using System.Text.Json.Nodes;

using Oadm.Sdk.Compliance;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.EdgeStorage.Tests;

/// <summary>The compliance check "SD card" on the recorded disk lists: no card fails, a card that is not in use fails, free space.</summary>
public sealed class SdCardCheckTests
{
    private static readonly SdCardCheck Check = new();

    private static FakeCamera Camera(string fixture) => new()
    {
        Respond = (request, _) => request.RequestUri!.ToString().Contains("disks/list.cgi", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture))) }
            : null,
    };

    private static Task<ComplianceResult> Evaluate(FakeCamera camera, JsonObject settings) =>
        Check.EvaluateAsync(new FakeComplianceContext(new FakeDevice(), camera), FakeComplianceContext.Invocation(settings), CancellationToken.None);

    [Fact]
    public async Task AWorkingCardPassesWithItsFreeSpace()
    {
        var result = await Evaluate(Camera("q1615-disks.xml"), []);
        Assert.Equal((ComplianceOutcome.Passed, "55.2 GB free of 55.2 GB (100 %)"), (result.Outcome, result.Detail));
    }

    [Fact]
    public async Task NoCardOrACardThatIsNotInUseFails()
    {
        var none = await Evaluate(Camera("m3215-no-card-disks.xml"), []);
        Assert.Equal((ComplianceOutcome.Failed, "No SD card"), (none.Outcome, none.Detail));

        var unmounted = await Evaluate(Camera("q1615-disks-unmounted.xml"), []);
        Assert.Equal((ComplianceOutcome.Failed, "The SD card is not in use"), (unmounted.Outcome, unmounted.Detail));
    }

    [Fact]
    public async Task TooLittleFreeSpaceFails()
    {
        var camera = Camera("q1615-disks.xml");
        var gb = await Evaluate(camera, new JsonObject { ["minFreeGb"] = 64 });
        Assert.Equal((ComplianceOutcome.Failed, "55.2 GB free of 55.2 GB (100 %), needs 64 GB"), (gb.Outcome, gb.Detail));
        Assert.Equal(ComplianceOutcome.Passed, (await Evaluate(camera, new JsonObject { ["minFreeGb"] = 10, ["minFreePercent"] = 50 })).Outcome);
        Assert.Equal(0, camera.Writes);
        Assert.Equal("SD card works, at least 10 GB free", Check.Describe(System.Text.Json.JsonSerializer.SerializeToElement(new { minFreeGb = 10 })));
    }
}
