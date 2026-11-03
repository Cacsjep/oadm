using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Plugins.Ocr.Tests.Readers;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests;

/// <summary>
/// Device groups (docs/spec/device-scope.md, Plugins and Shared objects): readers belong to the device scope that made them;
/// no method reaches a camera outside the caller's group; a reader reads only while its camera is in its owner's devices.
/// </summary>
public sealed class DeviceGroupTests
{
    [Fact]
    public async Task NoMethodReachesADeviceOutsideTheUsersGroup()
    {
        await using var ocr = await OcrHarness.StartAsync();

        // The reader-id methods find only the caller's own readers (an unknown id here); their scope cases are below.
        await new PluginCanary(ocr.Context, ocr.Plugin, ocr.Inside, ocr.Outside)
            .WithoutDevices(OcrMethods.ListReaders, "the readers of the caller's own device scope")
            .WithoutDevices(OcrMethods.GetReader, "a reader of the caller's own device scope", """{"readerId":"r1"}""")
            .Method(OcrMethods.SaveReader, """{"reader":{"name":"Gate","deviceId":"{device}"}}""")
            .Method(OcrMethods.GetCameraOptions, """{"deviceId":"{device}"}""")
            .WithoutDevices(OcrMethods.GetEngine, "what reads the pictures on this server")
            .AllDevicesOnly(OcrMethods.SetUseGpu, """{"useGpu":false}""")
            .WithoutDevices(OcrMethods.DeleteReader, "a reader of the caller's own device scope", """{"readerId":"r1"}""")
            .Method(OcrMethods.StartPreview, """{"reader":{"name":"Gate","deviceId":"{device}"}}""")
            .WithoutDevices(OcrMethods.StopPreview, "a preview of the caller's own device scope", """{"previewId":"p1"}""")
            .WithoutDevices(OcrMethods.GetPreview, "a preview of the caller's own device scope", """{"previewId":"p1"}""")
            .WithoutDevices(OcrMethods.GetReading, "a reader of the caller's own device scope", """{"readerId":"r1"}""")
            .RunAsync();
    }

    private static (FakeCallContext Group, string Key) GroupOf(OcrHarness ocr, string userName = "operator")
    {
        var key = ocr.Context.FakeScopes.Add("Building B", [ocr.Inside.Id]);
        return (FakeCallContext.WithGroup(ocr.Context, [ocr.Inside.Id], userName, key), key);
    }

    [Fact]
    public async Task AGroupSeesOnlyItsOwnReadersAndAnotherOnesAnswersLikeAnUnknownId()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var (group, key) = GroupOf(ocr);
        var admins = await ocr.SaveAsync(ocr.NewReader("Lobby"));
        var mine = await ocr.SaveAsync(ocr.NewReader("Gate"), group);
        ocr.Frames.Single = "A";

        Assert.Equal(["Gate"], (await ocr.ListAsync(group)).Readers.Select(r => r.Name));
        Assert.Equal(["Gate", "Lobby"], (await ocr.ListAsync()).Readers.Select(r => r.Name));
        Assert.Equal(key, ocr.Plugin.FindReader(mine.Id)!.OwnerScopeKey);
        Assert.Equal(key, ocr.Plugin.EventTypes.Single(t => t.Id == OcrInfo.TextReadTypeId(mine.Id)).OwnerScopeKey);

        foreach (var method in new[] { OcrMethods.GetReader, OcrMethods.GetReading })
        {
            var foreign = await Assert.ThrowsAsync<ArgumentException>(() => ocr.CallAsync(method, new ReaderRequest { ReaderId = admins.Id }, group));
            var unknown = await Assert.ThrowsAsync<ArgumentException>(() => ocr.CallAsync(method, new ReaderRequest { ReaderId = "unknown" }, group));
            Assert.Equal(unknown.Message, foreign.Message);
        }

        var change = await Assert.ThrowsAsync<ArgumentException>(() => ocr.SaveAsync(admins with { Name = "Mine now" }, group));
        Assert.Equal(ReaderRules.Gone, change.Message);
        await ocr.CallAsync(OcrMethods.DeleteReader, new ReaderRequest { ReaderId = admins.Id }, group);
        Assert.NotNull(ocr.Plugin.FindReader(admins.Id));
        Assert.Equal(0, ocr.Frames.Singles);

        // The same name in another scope is no conflict; a user with all devices changes the group's reader and it stays the group's.
        await ocr.SaveAsync(ocr.NewReader("Lobby"), group);
        var edited = await ocr.SaveAsync(mine with { Name = "Gate 2" });
        Assert.Equal(key, ocr.Plugin.FindReader(edited.Id)!.OwnerScopeKey);
        await ocr.ReadOnceAsync(mine.Id);
        Assert.Equal(1, ocr.Frames.Singles);
    }

    [Fact]
    public async Task AGroupsReaderTakesOnlyItsCamerasAndAUserWithoutDevicesSavesNone()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var (group, _) = GroupOf(ocr);
        var mine = await ocr.SaveAsync(ocr.NewReader("Gate"), group);

        // A saved reader keeps its camera (also a group's: never one outside the group); a new one only on the caller's cameras.
        await Assert.ThrowsAsync<ReaderFieldException>(() => ocr.SaveAsync(mine with { DeviceId = ocr.Outside.Id }));
        await Assert.ThrowsAsync<DevicesNotAvailableException>(() => ocr.SaveAsync(ocr.NewReader("Other", device: Guid.NewGuid())));

        var none = FakeCallContext.WithGroup(ocr.Context, [], "nobody", "none");
        var refused = await Assert.ThrowsAsync<DeviceAccessRefusedException>(() => ocr.SaveAsync(ocr.NewReader("Nobody's"), none));
        Assert.Equal(DeviceGroupMessages.NoDevicesRefusal, refused.Message);
        Assert.Empty((await ocr.ListAsync(none)).Readers);
    }

    [Fact]
    public async Task ADeletedGroupsReadersReadNothing()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var (group, key) = GroupOf(ocr);
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching), group);
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the group's reader watches");

        ocr.Context.FakeScopes.Delete(key);

        await Eventually.TrueAsync(() => ocr.Frames.Open == 0, "the deleted group's reader stopped");
        Assert.Equal(ReaderRules.Off, Assert.Single((await ocr.ListAsync()).Readers).Problem);
        var action = Assert.Single(ocr.Plugin.ActionPlugins);
        var off = await Assert.ThrowsAsync<InvalidOperationException>(() => ocr.ReadOnceAsync(reader.Id));
        Assert.Equal(ReaderRules.Off, off.Message);
        Assert.Equal(0, ocr.Frames.Singles);

        // A user with all devices saves it: it belongs to all devices and reads again.
        await ocr.SaveAsync(reader);
        Assert.Null(ocr.Plugin.FindReader(reader.Id)!.OwnerScopeKey);
        await Eventually.TrueAsync(() => ocr.Frames.Open == 1, "the reader watches again");
        Assert.Null(Assert.Single((await ocr.ListAsync()).Readers).Problem);
    }

    [Fact]
    public async Task ThePagesOfAGroupHearOnlyOfTheirReaders()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var (group, _) = GroupOf(ocr);
        var other = FakeCallContext.WithGroup(ocr.Context, [ocr.Inside.Id], "other", ocr.Context.FakeScopes.Add("Building C", [ocr.Inside.Id]));
        var mine = await ocr.SaveAsync(ocr.NewReader("Gate"), group);
        await ocr.SaveAsync(ocr.NewReader("Lobby"));
        ocr.Frames.Single = "A";
        await ocr.ReadOnceAsync(mine.Id);

        var seen = ocr.Context.EventsSeenBy(group);
        Assert.Equal(["Gate"], OcrJson.Deserialize<ReaderList>(seen.Last(e => e.Topic == OcrMethods.ReadersTopic).PayloadJson).Readers.Select(r => r.Name));
        Assert.Equal(mine.Id, OcrJson.Deserialize<ReadingEvent>(Assert.Single(seen, e => e.Topic == OcrMethods.ReadingTopic).PayloadJson).Reading.ReaderId);
        Assert.Empty(ocr.Context.EventsSeenBy(other));
        var admin = ocr.Context.EventsSeenBy(ocr.Context.CallAsAllDevices());
        Assert.Equal(["Gate", "Lobby"], OcrJson.Deserialize<ReaderList>(admin.Last(e => e.Topic == OcrMethods.ReadersTopic).PayloadJson).Readers.Select(r => r.Name));
    }
}
