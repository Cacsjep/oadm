using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Sdk.Compliance;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Acap.Tests;

/// <summary>The compliance check "Application running": installed, running, version, license; one list per run; read only.</summary>
public sealed class ApplicationRunningCheckTests
{
    private static readonly ApplicationRunningCheck Check = new();

    private static FakeAcapDevice Camera()
    {
        var camera = new FakeAcapDevice();
        camera.Apps["objectanalytics"] = new InstalledApplication
        {
            Name = "objectanalytics", NiceName = "AXIS Object Analytics", Version = "1.26.205", Status = "Running", License = "None",
        };
        camera.Apps["fflprapp"] = new InstalledApplication
        {
            Name = "fflprapp", NiceName = "AXIS License Plate Verifier", Version = "3.0.13", Status = "Stopped", License = "Missing",
        };
        return camera;
    }

    private static Task<ComplianceResult> Evaluate(FakeComplianceContext ctx, JsonObject settings) =>
        Check.EvaluateAsync(ctx, FakeComplianceContext.Invocation(settings), CancellationToken.None);

    [Fact]
    public async Task ARunningApplicationPassesAndAStoppedOneFails()
    {
        var camera = Camera();
        var ctx = new FakeComplianceContext(new FakeDevice(), camera);

        var running = await Evaluate(ctx, new JsonObject { ["application"] = "objectanalytics", ["state"] = "running" });
        Assert.Equal((ComplianceOutcome.Passed, "AXIS Object Analytics 1.26.205 running"), (running.Outcome, running.Detail));

        var stopped = await Evaluate(ctx, new JsonObject { ["application"] = "fflprapp", ["state"] = "running" });
        Assert.Equal((ComplianceOutcome.Failed, "AXIS License Plate Verifier is stopped"), (stopped.Outcome, stopped.Detail));

        var installed = await Evaluate(ctx, new JsonObject { ["application"] = "fflprapp", ["state"] = "installed" });
        Assert.Equal(ComplianceOutcome.Passed, installed.Outcome);

        var missing = await Evaluate(ctx, new JsonObject { ["application"] = "parking", ["state"] = "installed" });
        Assert.Equal((ComplianceOutcome.Failed, "parking is not installed"), (missing.Outcome, missing.Detail));

        // Four checks, one list: the run shares it; nothing is written.
        Assert.Equal(1, ctx.ReadCount);
        Assert.Single(camera.Requests);
        Assert.Empty(camera.Writes);
    }

    [Fact]
    public async Task VersionAndLicenseAreCheckedWhenAsked()
    {
        var ctx = new FakeComplianceContext(new FakeDevice(), Camera());

        var older = await Evaluate(ctx, new JsonObject { ["application"] = "objectanalytics", ["state"] = "running", ["minimumVersion"] = "1.30" });
        Assert.Equal((ComplianceOutcome.Failed, "AXIS Object Analytics 1.26.205, needs 1.30"), (older.Outcome, older.Detail));
        Assert.Equal(ComplianceOutcome.Passed, (await Evaluate(ctx, new JsonObject { ["application"] = "objectanalytics", ["state"] = "running", ["minimumVersion"] = "1.26" })).Outcome);

        var noLicense = await Evaluate(ctx, new JsonObject { ["application"] = "fflprapp", ["state"] = "installed", ["license"] = true });
        Assert.Equal((ComplianceOutcome.Failed, "AXIS License Plate Verifier: license missing"), (noLicense.Outcome, noLicense.Detail));
        Assert.Equal(ComplianceOutcome.Passed, (await Evaluate(ctx, new JsonObject { ["application"] = "objectanalytics", ["state"] = "running", ["license"] = true })).Outcome);
    }

    [Fact]
    public async Task TheApplicationsOfAChosenDeviceAreTheChoices()
    {
        var choices = await Check.ListChoicesAsync(new FakeComplianceQueryContext(new FakeDevice(), Camera()), "application", CancellationToken.None);
        Assert.Equal(["AXIS License Plate Verifier", "AXIS Object Analytics"], choices.Select(c => c.Label));
        Assert.Equal("objectanalytics", choices[1].Value);
        Assert.Empty(await Check.ListChoicesAsync(new FakeComplianceQueryContext(), "application", CancellationToken.None));
        Assert.True(Check.Fields.Single(f => f.Key == "application").ChoicesFromDevice);
    }

    [Fact]
    public void SettingsAreCheckedAndDescribed()
    {
        Assert.Equal("Enter a version like 1.4.", Check.Validate(JsonSerializer.SerializeToElement(new { minimumVersion = "new" }))["minimumVersion"]);
        Assert.Empty(Check.Validate(JsonSerializer.SerializeToElement(new { minimumVersion = "1.4" })));
        Assert.Equal(
            "objectanalytics running, at least 1.4, license valid",
            Check.Describe(JsonSerializer.SerializeToElement(new { application = "objectanalytics", state = "running", minimumVersion = "1.4", license = true })));
    }
}
