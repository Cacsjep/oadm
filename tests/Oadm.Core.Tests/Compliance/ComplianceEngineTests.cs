using System.Text.Json.Nodes;

using Oadm.Core.Compliance;
using Oadm.Sdk.Compliance;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Events;
using Oadm.Sdk.Testing;

namespace Oadm.Core.Tests.Compliance;

public sealed class ComplianceEngineTests
{
    private static readonly Guid Reachable = BuiltInComplianceTypes.ReachableCheckId;
    private static readonly Guid LoginWorks = BuiltInComplianceTypes.LoginWorksCheckId;

    [Fact]
    public async Task TheDefaultChecksShowWhoCannotBeReachedOrLoggedInTo()
    {
        await using var rig = await ComplianceRig.StartAsync();
        var ok = await rig.AddDeviceAsync("10.0.0.1");
        var gone = await rig.AddDeviceAsync("10.0.0.2", DeviceStatus.Unreachable);
        var locked = await rig.AddDeviceAsync("10.0.0.3", DeviceStatus.CredentialsRequired);

        await rig.WaitForAsync(Reachable, ok.Id, ComplianceOutcome.Passed);
        await rig.WaitForAsync(Reachable, gone.Id, ComplianceOutcome.Failed);
        await rig.WaitForAsync(LoginWorks, locked.Id, ComplianceOutcome.Failed);
        Assert.Equal(ComplianceOutcome.NotChecked, rig.Result(LoginWorks, gone.Id)!.Outcome); // Reachable reports it
        Assert.Equal("Credentials required", rig.Result(LoginWorks, locked.Id)!.Detail);

        var checks = rig.Engine.ListChecks();
        Assert.Equal(["Reachable", "Login works"], checks.Select(c => c.Check.Name).Order().Reverse());
        Assert.All(checks, c => Assert.True(c.Check.BuiltIn));
        await Assert.ThrowsAsync<ComplianceValidationException>(() => rig.Service.DeleteAsync(Reachable, CancellationToken.None));
        await Assert.ThrowsAsync<ComplianceValidationException>(() => rig.Service.SetEnabledAsync(Reachable, false, CancellationToken.None));

        // A device that answers again passes at once.
        await rig.Devices.UpdateAsync(gone.Id, d => d.Status = DeviceStatus.Ok, CancellationToken.None);
        await rig.WaitForAsync(Reachable, gone.Id, ComplianceOutcome.Passed);
    }

    [Fact]
    public async Task TheCertificateCheckUsesTheDaysTheUserSet()
    {
        await using var rig = await ComplianceRig.StartAsync();
        var soon = await rig.AddDeviceAsync("10.0.0.1");
        var fine = await rig.AddDeviceAsync("10.0.0.2");
        var http = await rig.AddDeviceAsync("10.0.0.3");
        var now = rig.Time.GetUtcNow().UtcDateTime;
        await rig.Devices.UpdateAsync(soon.Id, d => d.CertNotAfterUtc = now.AddDays(10.5), CancellationToken.None);
        await rig.Devices.UpdateAsync(fine.Id, d => d.CertNotAfterUtc = now.AddDays(200), CancellationToken.None);
        await rig.Devices.UpdateAsync(http.Id, d => d.HttpsEnabled = false, CancellationToken.None);

        var check = await rig.SaveAsync(BuiltInComplianceTypes.CertificateId, new JsonObject { ["expired"] = true, ["expiresSoon"] = true, ["days"] = 30 });

        await rig.WaitForAsync(check.Id, soon.Id, ComplianceOutcome.Failed);
        Assert.Equal("10 days left", rig.Result(check.Id, soon.Id)!.Detail);
        await rig.WaitForAsync(check.Id, fine.Id, ComplianceOutcome.Passed);
        await rig.WaitForAsync(check.Id, http.Id, ComplianceOutcome.NotApplicable);
        Assert.Equal(0, check.IntervalMinutes); // cached data: no interval
    }

    [Fact]
    public async Task AReadCheckReadsEachDeviceOnceForAllItsChecksAndNeverContactsRefusedDevices()
    {
        await using var rig = await ComplianceRig.StartAsync();
        var good = await rig.AddDeviceAsync("10.0.0.1");
        var bad = await rig.AddDeviceAsync("10.0.0.2");
        var locked = await rig.AddDeviceAsync("10.0.0.3", DeviceStatus.CredentialsRequired);
        rig.Plugin.Read.Failing[bad.Id] = true;
        rig.Plugin.Second.Failing[bad.Id] = true;

        var first = await rig.SaveAsync("test.read", new JsonObject { ["value"] = "a" }, name: "First");
        var second = await rig.SaveAsync("test.read-second", new JsonObject { ["value"] = "b" }, name: "Second");

        await rig.WaitForAsync(first.Id, good.Id, ComplianceOutcome.Passed);
        await rig.WaitForAsync(first.Id, bad.Id, ComplianceOutcome.Failed);
        await rig.WaitForAsync(second.Id, bad.Id, ComplianceOutcome.Failed);
        Assert.Equal("Bad a", rig.Result(first.Id, bad.Id)!.Detail);
        Assert.Equal(ComplianceOutcome.NotChecked, rig.Result(first.Id, locked.Id)!.Outcome);
        Assert.Equal(DeviceMessages.CredentialsRequired, rig.Result(first.Id, locked.Id)!.Detail);
        Assert.DoesNotContain(locked.Id, rig.Plugin.Read.Evaluations);

        // The check event is on for the device the check fails on.
        await Eventually.TrueAsync(() => rig.Events.States.TryGetValue(ComplianceEngine.EventTypeId(first.Id), out var on) && on.SequenceEqual([bad.Id]), "event on for the failing device");
        Assert.Contains(rig.Engine.EventTypes, t => t.Id == ComplianceEngine.EventTypeId(first.Id) && t.DisplayName == "Compliance: First" && t.Kind == EventKind.Stateful);
    }

    [Fact]
    public async Task ChecksOfTheSameRunShareTheirReads()
    {
        await using var rig = await ComplianceRig.StartAsync();
        var device = await rig.AddDeviceAsync("10.0.0.1");
        rig.Plugin.Read.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = await rig.SaveAsync("test.read", name: "First");
        await Eventually.TrueAsync(() => rig.Plugin.Read.Evaluations.Contains(device.Id), "first run started");
        var second = await rig.SaveAsync("test.read-second", name: "Second"); // waits: the device is being read
        rig.Plugin.Read.Hold.SetResult();
        rig.Plugin.Read.Hold = null;
        await rig.WaitForAsync(first.Id, device.Id, ComplianceOutcome.Passed);
        await rig.WaitForAsync(second.Id, device.Id, ComplianceOutcome.Passed);

        // A new round of both checks after the interval: one run, one read.
        var before = rig.Plugin.Read.Reads[device.Id] + rig.Plugin.Second.Reads[device.Id];
        rig.Time.Advance(TimeSpan.FromMinutes(61));
        await Eventually.TrueAsync(() => rig.Plugin.Read.Reads[device.Id] + rig.Plugin.Second.Reads[device.Id] == before + 1, "one shared read for both checks");
    }

    [Fact]
    public async Task ReadChecksRunAgainAfterTheirIntervalAndOnCheckNow()
    {
        await using var rig = await ComplianceRig.StartAsync();
        var device = await rig.AddDeviceAsync("10.0.0.1");
        var check = await rig.SaveAsync("test.read", interval: 60);
        await rig.WaitForAsync(check.Id, device.Id, ComplianceOutcome.Passed);
        Assert.Single(rig.Plugin.Read.Evaluations);

        rig.Plugin.Read.Failing[device.Id] = true;
        rig.Time.Advance(TimeSpan.FromMinutes(30));
        await Eventually.StaysTrueAsync(() => rig.Plugin.Read.Evaluations.Count == 1, "no read before the interval", TimeSpan.FromMilliseconds(200));

        rig.Time.Advance(TimeSpan.FromMinutes(30));
        await rig.WaitForAsync(check.Id, device.Id, ComplianceOutcome.Failed);
        var failedSince = rig.Result(check.Id, device.Id)!.SinceUtc;

        rig.Engine.CheckNow([check.Id], [device.Id]);
        await Eventually.TrueAsync(() => rig.Plugin.Read.Evaluations.Count == 3, "Check now reads again");
        Assert.Equal(failedSince, rig.Result(check.Id, device.Id)!.SinceUtc); // still failing since the same time
    }

    [Fact]
    public async Task AFailingOrSlowCheckIsCouldNotCheckWithTheReason()
    {
        await using var rig = await ComplianceRig.StartAsync();
        var device = await rig.AddDeviceAsync("10.0.0.1");
        rig.Plugin.Read.ThrowWith = "Not Found - HTTP 404 (API not available on this firmware)";
        var check = await rig.SaveAsync("test.read");
        await rig.WaitForAsync(check.Id, device.Id, ComplianceOutcome.CouldNotCheck);
        Assert.Equal("Not Found - HTTP 404 (API not available on this firmware)", rig.Result(check.Id, device.Id)!.Detail);

        rig.Plugin.Read.ThrowWith = null;
        rig.Plugin.Read.Hold = new TaskCompletionSource(); // never answers
        var evaluations = rig.Plugin.Read.Evaluations.Count;
        rig.Engine.CheckNow([check.Id], null);
        await Eventually.TrueAsync(() => rig.Plugin.Read.Evaluations.Count > evaluations, "read started");
        rig.Time.Advance(TimeSpan.FromSeconds(15));
        await Eventually.TrueAsync(() => rig.Result(check.Id, device.Id)!.Detail == "Timeout after 15 s", "timeout");
    }

    [Fact]
    public async Task DevicesFollowTheTagsOfTheCheck()
    {
        await using var rig = await ComplianceRig.StartAsync();
        var parking = await rig.AddDeviceAsync("10.0.0.1", DeviceStatus.Ok, "Parking");
        var lobby = await rig.AddDeviceAsync("10.0.0.2", DeviceStatus.Ok, "Lobby");
        var check = await rig.SaveAsync("test.read", devices: new DeviceSelector { Mode = DeviceSelectorMode.Devices, Tags = ["parking"] });

        await rig.WaitForAsync(check.Id, parking.Id, ComplianceOutcome.Passed);
        Assert.Null(rig.Result(check.Id, lobby.Id));

        await rig.Devices.UpdateAsync(lobby.Id, d => d.Tags = ["Lobby", "Parking"], CancellationToken.None);
        await rig.WaitForAsync(check.Id, lobby.Id, ComplianceOutcome.Passed, "a tagged device joins the check");
        await rig.Devices.UpdateAsync(parking.Id, d => d.Tags = [], CancellationToken.None);
        await Eventually.TrueAsync(() => rig.Result(check.Id, parking.Id) is null, "an untagged device leaves the check");

        await rig.Devices.RemoveAsync(lobby.Id, CancellationToken.None);
        await Eventually.TrueAsync(() => rig.Result(check.Id, lobby.Id) is null && rig.Result(Reachable, lobby.Id) is null, "a removed device has no results");
    }

    [Fact]
    public async Task ResultsSurviveARestartWithTheirTimes()
    {
        await using var rig = await ComplianceRig.StartAsync();
        var device = await rig.AddDeviceAsync("10.0.0.1");
        rig.Plugin.Read.Failing[device.Id] = true;
        var check = await rig.SaveAsync("test.read");
        await rig.WaitForAsync(check.Id, device.Id, ComplianceOutcome.Failed);
        var before = rig.Result(check.Id, device.Id)!;
        await rig.Engine.FlushAsync(CancellationToken.None);

        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.StartEngineAsync();
        var after = rig.Result(check.Id, device.Id)!;
        Assert.Equal(ComplianceOutcome.Failed, after.Outcome);
        Assert.Equal(before.SinceUtc, after.SinceUtc);
        Assert.Equal(before.CheckedUtc, after.CheckedUtc);
        await Eventually.StaysTrueAsync(() => rig.Plugin.Read.Evaluations.Count == 1, "not read again before the interval", TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task ChangedSettingsCheckEveryDeviceAgain()
    {
        await using var rig = await ComplianceRig.StartAsync();
        var device = await rig.AddDeviceAsync("10.0.0.1");
        rig.Plugin.Read.Failing[device.Id] = true;
        var check = await rig.SaveAsync("test.read", new JsonObject { ["value"] = "a" });
        await Eventually.TrueAsync(() => rig.Result(check.Id, device.Id)?.Detail == "Bad a", "first settings");

        check.Settings["value"] = "b";
        var saved = await rig.SaveAsync(check);
        Assert.Equal(check.Version + 1, saved.Version);
        await Eventually.TrueAsync(() => rig.Result(check.Id, device.Id)?.Detail == "Bad b", "new settings");

        // A save of the older version is refused (two users).
        check.Settings["value"] = "c";
        var refused = await Assert.ThrowsAsync<ComplianceValidationException>(() => rig.SaveAsync(check));
        Assert.StartsWith("Another user changed the check", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChecksPluginTurnedOffStopsItsChecks()
    {
        await using var rig = await ComplianceRig.StartAsync();
        var device = await rig.AddDeviceAsync("10.0.0.1");
        rig.Plugin.Read.Failing[device.Id] = true;
        var check = await rig.SaveAsync("test.read");
        await rig.WaitForAsync(check.Id, device.Id, ComplianceOutcome.Failed);

        rig.Registry.SetEnabled(rig.PluginOrigin.PackageId, false);
        await Eventually.TrueAsync(
            () => rig.Engine.ListChecks().Single(c => c.Check.Id == check.Id).Problem == "Not available: Test checks is turned off",
            "the check says why it does not run");
        await Eventually.TrueAsync(() => rig.Events.States[ComplianceEngine.EventTypeId(check.Id)].Count == 0, "its event is off");
        var evaluations = rig.Plugin.Read.Evaluations.Count;
        rig.Engine.CheckNow(null, null);
        rig.Time.Advance(TimeSpan.FromHours(2));
        await Eventually.StaysTrueAsync(() => rig.Plugin.Read.Evaluations.Count == evaluations, "no reads while off", TimeSpan.FromMilliseconds(200));
        Assert.Equal("Not available: Test checks is turned off.", rig.Service.Problem(new ComplianceCheck { Name = "x", TypeId = "test.read", IntervalMinutes = 60 }));
    }

    [Theory]
    [InlineData("", "test.read", 60, "Enter a name.")]
    [InlineData("x", "nope", 60, "Choose the kind of check.")]
    [InlineData("x", "test.read", 5, "Check every 15 to 1440 minutes.")]
    [InlineData("x", BuiltInComplianceTypes.FirmwareId, 0, "At least AXIS OS: Enter at least axis os.")]
    public async Task ChecksAreValidatedBeforeTheyAreSaved(string name, string type, int interval, string problem)
    {
        await using var rig = await ComplianceRig.StartAsync();
        Assert.Equal(problem, rig.Service.Problem(new ComplianceCheck { Name = name, TypeId = type, IntervalMinutes = interval }));
    }

    [Fact]
    public async Task ACheckNeedsDevicesAndAValidFirmwareVersion()
    {
        await using var rig = await ComplianceRig.StartAsync();
        Assert.Equal("Choose at least one device or tag.", rig.Service.Problem(new ComplianceCheck
        {
            Name = "x", TypeId = "test.read", IntervalMinutes = 60, Devices = new DeviceSelector { Mode = DeviceSelectorMode.Devices },
        }));
        Assert.Equal("At least AXIS OS: Enter a version like 12.5.56.", rig.Service.Problem(new ComplianceCheck
        {
            Name = "x", TypeId = BuiltInComplianceTypes.FirmwareId, Settings = new JsonObject { ["minimum"] = "twelve" },
        }));

        var old = await rig.AddDeviceAsync("10.0.0.1");
        await rig.Devices.UpdateAsync(old.Id, d => d.FirmwareVersion = "10.12.240", CancellationToken.None);
        var check = await rig.SaveAsync(BuiltInComplianceTypes.FirmwareId, new JsonObject { ["minimum"] = "11.11" });
        await rig.WaitForAsync(check.Id, old.Id, ComplianceOutcome.Failed);
        Assert.Equal("AXIS OS 10.12.240, needs 11.11", rig.Result(check.Id, old.Id)!.Detail);
    }
}
