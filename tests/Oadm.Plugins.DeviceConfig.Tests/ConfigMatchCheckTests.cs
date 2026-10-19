using System.Text.Json.Nodes;

using Oadm.Plugins.DeviceConfig.Configure;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>The compliance check "Matches configuration file" against the fake camera the ADM file was made from.</summary>
public sealed class ConfigMatchCheckTests
{
    private static readonly ConfigMatchCheck Check = new();

    private static JsonObject Settings(string fixture) => new()
    {
        ["file"] = new JsonObject { ["name"] = fixture, ["data"] = Convert.ToBase64String(ConfigureFixtures.Bytes(fixture)) },
    };

    private static Task<ComplianceResult> Evaluate(ConfigureCamera camera, string fixture) =>
        Check.EvaluateAsync(new FakeComplianceContext(new FakeDevice(), camera), FakeComplianceContext.Invocation(Settings(fixture)), CancellationToken.None);

    [Fact]
    public async Task TheCameraTheFileWasMadeFromMatches()
    {
        var camera = new ConfigureCamera();
        var result = await Evaluate(camera, "adm-p3265v-12.11.77.cfg");
        Assert.Equal(ComplianceOutcome.Passed, result.Outcome);
        Assert.EndsWith("settings match", result.Detail, StringComparison.Ordinal);
        Assert.Empty(camera.Updates); // read only
    }

    [Fact]
    public async Task AChangedParameterOrAMissingRuleIsNamed()
    {
        var camera = new ConfigureCamera();
        camera.Values["ImageSource.I0.CaptureFrequency"] = "50Hz";
        var rule = camera.Rules[0];
        camera.Rules.Clear();

        var result = await Evaluate(camera, "adm-p3265v-12.11.77.cfg");
        Assert.Equal(ComplianceOutcome.Failed, result.Outcome);
        Assert.StartsWith("2 settings differ", result.Detail, StringComparison.Ordinal);
        Assert.Contains("ImageSource.I0.CaptureFrequency: 50Hz (file: 60Hz)", result.Detail, StringComparison.Ordinal);
        Assert.Contains($"Action rule {rule.Name}: missing", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParametersAnotherModelDoesNotHaveAreSkippedAndAddressesAreNeverCompared()
    {
        var file = AdmConfigFile.Write(new AdmConfig
        {
            Parameters =
            [
                new NameValue("ImageSource.I0.CaptureFrequency", "60Hz"),
                new NameValue("Image.I9.Unknown", "1"),
                new NameValue("Network.IPAddress", "10.9.9.9"),
            ],
        });
        var settings = new JsonObject { ["file"] = new JsonObject { ["name"] = "mixed.cfg", ["data"] = Convert.ToBase64String(file) } };
        var result = await Check.EvaluateAsync(new FakeComplianceContext(new FakeDevice(), new ConfigureCamera()), FakeComplianceContext.Invocation(settings), CancellationToken.None);
        Assert.Equal((ComplianceOutcome.Passed, "1 setting matches (1 not on this model)"), (result.Outcome, result.Detail));
    }

    [Fact]
    public void AFileThatIsNotAConfigurationFileIsRefused()
    {
        var settings = System.Text.Json.JsonSerializer.SerializeToElement(new JsonObject
        {
            ["file"] = new JsonObject { ["name"] = "x.cfg", ["data"] = Convert.ToBase64String("<html/>"u8.ToArray()) },
        });
        Assert.Equal(AdmConfigFile.NotAnAdmFile, Check.Validate(settings)["file"]);
        Assert.Empty(Check.Validate(System.Text.Json.JsonSerializer.SerializeToElement(Settings("adm-p3265v-12.11.77.cfg"))));
        Assert.StartsWith("Matches adm-p3265v-12.11.77.cfg (", Check.Describe(System.Text.Json.JsonSerializer.SerializeToElement(Settings("adm-p3265v-12.11.77.cfg"))), StringComparison.Ordinal);
    }
}
