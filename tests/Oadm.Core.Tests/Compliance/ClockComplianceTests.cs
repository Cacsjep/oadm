using System.Text.Json;
using System.Text.Json.Nodes;

using Oadm.Core.Compliance;
using Oadm.Core.Devices;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.Compliance;

/// <summary>The host check type Clock (docs/spec/compliance.md "Types"): cached values only.</summary>
public sealed class ClockComplianceTests
{
    private static IComplianceCheckType Type => BuiltInComplianceTypes.All.Single(t => t.Id == BuiltInComplianceTypes.ClockId);

    [Fact]
    public void TheTypeHasTheAgreedIdAndFields()
    {
        Assert.Equal("oadm.compliance.clock", Type.Id);
        Assert.True(Type.UsesCachedDataOnly);
        var allowed = Assert.Single(Type.Fields, f => f.Key == "allowedSeconds");
        Assert.Equal("Allowed difference (s)", allowed.Label);
        Assert.Equal(("2", 1, 3600), (allowed.Default, allowed.Min, allowed.Max));
        var ntp = Assert.Single(Type.Fields, f => f.Key == "ntpSynced");
        Assert.Equal("NTP must be synced", ntp.Label);
        Assert.Equal("true", ntp.Default);
        Assert.Equal("Clock within 2 s, NTP synced", Type.Describe(Settings(2, true)));
        Assert.Equal("Clock within 30 s", Type.Describe(Settings(30, false)));
    }

    [Theory]
    [InlineData(1_500, NtpState.Synced, 2, true, ComplianceOutcome.Passed, "2 s ahead")]
    [InlineData(50_000, NtpState.Synced, 2, true, ComplianceOutcome.Failed, "50 s ahead")]
    [InlineData(-50_000, NtpState.Synced, 60, true, ComplianceOutcome.Passed, "50 s behind")]
    [InlineData(0, NtpState.NotSynced, 2, true, ComplianceOutcome.Failed, "NTP not synced")]
    [InlineData(0, NtpState.Off, 2, true, ComplianceOutcome.Failed, "NTP off")]
    [InlineData(0, NtpState.Off, 2, false, ComplianceOutcome.Passed, "OK")]
    [InlineData(0, NtpState.Unknown, 2, true, ComplianceOutcome.Passed, "OK")] // no NTP API: the offset decides
    public async Task TheOffsetAndNtpDecide(int offset, NtpState ntp, int allowed, bool ntpSynced, ComplianceOutcome outcome, string detail)
    {
        var result = await EvaluateAsync(new Device { Status = DeviceStatus.Ok, ClockOffsetMs = offset, NtpState = ntp }, Settings(allowed, ntpSynced));
        Assert.Equal((outcome, detail), (result.Outcome, result.Detail));
    }

    [Fact]
    public async Task AnUnknownOffsetIsNotMeasuredYet()
    {
        var result = await EvaluateAsync(new Device { Status = DeviceStatus.Ok, NtpState = NtpState.NotSynced }, Settings(2, true));
        Assert.Equal((ComplianceOutcome.NotChecked, "Not measured yet"), (result.Outcome, result.Detail));
    }

    [Fact]
    public async Task ADeviceThatIsNotOkIsNotChecked()
    {
        var result = await EvaluateAsync(new Device { Status = DeviceStatus.CredentialsRequired, ClockOffsetMs = 99_000 }, Settings(2, true));
        Assert.Equal((ComplianceOutcome.NotChecked, DeviceMessages.CredentialsRequired), (result.Outcome, result.Detail));
    }

    [Fact]
    public async Task ACheckSavedWithoutSettingsUsesTheDefaults()
    {
        var empty = JsonDocument.Parse("{}").RootElement;
        Assert.Equal(ComplianceOutcome.Failed, (await EvaluateAsync(new Device { Status = DeviceStatus.Ok, ClockOffsetMs = 2_500 }, empty)).Outcome);
        Assert.Equal(ComplianceOutcome.Failed, (await EvaluateAsync(new Device { Status = DeviceStatus.Ok, ClockOffsetMs = 0, NtpState = NtpState.Off }, empty)).Outcome);
    }

    [Fact]
    public async Task TheEngineFollowsTheDevice()
    {
        await using var rig = await ComplianceRig.StartAsync();
        var camera = await rig.AddDeviceAsync("10.0.0.1");
        var check = await rig.SaveAsync(BuiltInComplianceTypes.ClockId, new JsonObject { ["allowedSeconds"] = 5, ["ntpSynced"] = true });
        await rig.WaitForAsync(check.Id, camera.Id, ComplianceOutcome.NotChecked);

        await rig.Devices.UpdateAsync(camera.Id, d => d.ClockOffsetMs = -7_200_000, CancellationToken.None);
        await rig.WaitForAsync(check.Id, camera.Id, ComplianceOutcome.Failed);
        Assert.Equal("2 h behind", rig.Result(check.Id, camera.Id)!.Detail);

        await rig.Devices.UpdateAsync(camera.Id, d => d.ClockOffsetMs = 100, CancellationToken.None);
        await rig.WaitForAsync(check.Id, camera.Id, ComplianceOutcome.Passed);
    }

    private static JsonElement Settings(int allowed, bool ntpSynced) =>
        JsonSerializer.SerializeToElement(new JsonObject { ["allowedSeconds"] = allowed, ["ntpSynced"] = ntpSynced });

    private static Task<ComplianceResult> EvaluateAsync(Device device, JsonElement settings) =>
        Type.EvaluateAsync(new FakeComplianceContext(device), new ComplianceInvocation(Guid.NewGuid(), "Clock", settings), CancellationToken.None);
}
