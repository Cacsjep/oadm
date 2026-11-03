using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Frames;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Ocr.Tests.Readers;

/// <summary>
/// A reader across saves, deletes, stream failures and the action (docs/spec/ocr.md "Readers", "Runtime safety", "Events and
/// the rule action"): a rename goes on in the running loop, a reading never outlives its reader, a stream's failure is the
/// reader's problem, Read text answers a watching reader from its fresh reading, and the page never gets more than the
/// caller may see.
/// </summary>
public sealed class ReaderLifecycleTests
{
    private static async Task<Reader> WatchAsync(OcrHarness ocr, Reader reader)
    {
        var opened = ocr.Frames.Opened;
        var saved = await ocr.SaveAsync(reader);
        await Eventually.TrueAsync(() => ocr.Frames.Opened > opened && ocr.Frames.Open > 0, "the reader watches");
        return saved;
    }

    [Fact]
    public async Task ARenameKeepsTheStreamAndThePublishedValues()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await WatchAsync(ocr, ocr.NewReader("Gate", ReaderMode.Watching));
        await ocr.SendAndWaitAsync(ocr.Inside.Id, "A");
        await ocr.SendAndWaitAsync(ocr.Inside.Id, "A");
        Assert.Single(ocr.TextRead);

        await ocr.SaveAsync(reader with { Name = "Gate 1", Areas = [reader.Areas[0] with { Name = "Plate" }] });
        await ocr.SendAndWaitAsync(ocr.Inside.Id, "A");

        Assert.Equal(1, ocr.Frames.Opened); // the stream goes on
        Assert.Single(ocr.TextRead); // the value did not change: nothing published again
        Assert.Equal("A", Assert.Single((await ocr.ListAsync()).Readers).LastText); // the last reading still fits

        // A new area place starts the loop again.
        await ocr.SaveAsync(reader with { Areas = [reader.Areas[0] with { Box = new RelativeBox(0, 0, 0.5, 0.5) }] });
        await Eventually.TrueAsync(() => ocr.Frames.Opened == 2, "the reader watches anew");
        Assert.Empty((await ocr.ListAsync()).Readers[0].LastText);
    }

    [Fact]
    public async Task AReadingOfADeletedReaderIsNeitherKeptNorFired()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"));
        using var gate = new SemaphoreSlim(0);
        ocr.Recognizer.Gate = gate;
        ocr.Frames.Single = "A";

        var reading = ocr.ReadOnceAsync(reader.Id);
        await Eventually.TrueAsync(() => ocr.Recognizer.Started == 1, "the action reads");
        await ocr.CallAsync(OcrMethods.DeleteReader, new ReaderRequest { ReaderId = reader.Id });
        gate.Release();

        var gone = await Assert.ThrowsAsync<InvalidOperationException>(() => reading);
        Assert.Equal(ReaderRules.Gone, gone.Message);
        Assert.Empty(ocr.TextRead);
        Assert.Null(ocr.Plugin.Runtime!.Last(reader.Id));
    }

    [Fact]
    public async Task AReadingOfOldAreasIsNotKeptAsTheLast()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"));
        using var gate = new SemaphoreSlim(0);
        ocr.Recognizer.Gate = gate;
        ocr.Frames.Single = "A";

        var reading = ocr.ReadOnceAsync(reader.Id);
        await Eventually.TrueAsync(() => ocr.Recognizer.Started == 1, "the action reads");
        await ocr.SaveAsync(reader with { Areas = [reader.Areas[0] with { Box = new RelativeBox(0, 0, 0.5, 0.5) }] });
        gate.Release();
        await reading;

        Assert.Null(ocr.Plugin.Runtime!.Last(reader.Id)); // the page never shows the old areas' reading
        Assert.Single(ocr.TextRead); // what the rule asked for was read
    }

    [Fact]
    public async Task AStreamThatFailsGivesTheReaderItsProblemUntilPicturesComeAgain()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await WatchAsync(ocr, ocr.NewReader("Gate", ReaderMode.Watching));

        ocr.Frames.Tell(ocr.Inside.Id, FrameTexts.ResolutionNotOffered);
        Assert.Equal(FrameTexts.ResolutionNotOffered, Assert.Single((await ocr.ListAsync()).Readers).Problem);
        var told = OcrJson.Deserialize<ReaderList>(ocr.Context.PublishedEvents.Last(e => e.Topic == OcrMethods.ReadersTopic).PayloadJson);
        Assert.Equal(FrameTexts.ResolutionNotOffered, told.Readers[0].Problem);

        ocr.Frames.Tell(ocr.Inside.Id, null);
        Assert.Null(Assert.Single((await ocr.ListAsync()).Readers).Problem);
        Assert.Equal(reader.Id, ocr.Plugin.Runtime!.Watching.Single());
    }

    [Fact]
    public async Task ReadTextOnAWatchingReaderTakesItsFreshReading()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await WatchAsync(ocr, ocr.NewReader("Gate", ReaderMode.Watching));
        await ocr.SendAndWaitAsync(ocr.Inside.Id, "A");
        await ocr.SendAndWaitAsync(ocr.Inside.Id, "A");
        var published = ocr.TextRead.Count;

        var reply = await ocr.ReadOnceAsync(reader.Id);

        Assert.Equal("A", reply.Reading.Areas[0].Text);
        Assert.Equal(0, ocr.Frames.Singles); // no second picture: the stream read it just now
        Assert.Equal(published + 1, ocr.TextRead.Count); // every area's event again

        // Older than a second: a picture of its own (the stream sends nothing now).
        ocr.Time.Advance(ReaderRuntime.FreshReading + TimeSpan.FromMilliseconds(1));
        ocr.Frames.Single = "B";
        Assert.Equal("B", (await ocr.ReadOnceAsync(reader.Id)).Reading.Areas[0].Text);
        Assert.Equal(1, ocr.Frames.Singles);
    }

    [Fact]
    public async Task ReadTextWaitsForTheWatchingReadingInWork()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var reader = await WatchAsync(ocr, ocr.NewReader("Gate", ReaderMode.Watching));
        using var gate = new SemaphoreSlim(0);
        ocr.Recognizer.Gate = gate;
        ocr.Frames.Send(ocr.Inside.Id, "C");
        await Eventually.TrueAsync(() => ocr.Recognizer.Started == 1, "the stream's frame is read");

        var action = ocr.ReadOnceAsync(reader.Id);
        Assert.False(action.IsCompleted);
        gate.Release();

        Assert.Equal("C", (await action).Reading.Areas[0].Text);
        Assert.Equal(0, ocr.Frames.Singles);
    }

    [Fact]
    public async Task CutoutsOnlyForUsersWithLiveView()
    {
        await using var ocr = new OcrHarness(cutter: new FixedCutter());
        await ocr.Plugin.StartAsync(ocr.Context, CancellationToken.None);
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", areas: new Area { Name = "Code", Expected = ExpectedText.WithFormat("A"), Box = new RelativeBox(0, 0, 1, 1) }));
        ocr.Frames.Single = "A";
        await ocr.ReadOnceAsync(reader.Id);
        var payload = OcrJson.Serialize(new ReaderRequest { ReaderId = reader.Id });

        var withLiveView = OcrJson.Deserialize<ReadingReply>(await ocr.Plugin.InvokeAsync(ocr.Context.CallAsAllDevices(), OcrMethods.GetReading, payload, CancellationToken.None));
        Assert.NotNull(Assert.Single(withLiveView.Details).Cutout);

        var without = new WithoutLiveView(ocr.Context.CallAsAllDevices("viewer"));
        var reply = OcrJson.Deserialize<ReadingReply>(await ocr.Plugin.InvokeAsync(without, OcrMethods.GetReading, payload, CancellationToken.None));
        Assert.Equal("A", reply.Reading!.Areas[0].Text);
        Assert.Null(Assert.Single(reply.Details).Cutout);
    }

    [Fact]
    public async Task TheListShowsNoTextOfACameraTheCallerCannotSee()
    {
        await using var ocr = await OcrHarness.StartAsync();
        var key = ocr.Context.FakeScopes.Add("Building B", [ocr.Inside.Id]);
        var saver = FakeCallContext.WithGroup(ocr.Context, [ocr.Inside.Id], "operator", key);
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate"), saver);
        ocr.Frames.Single = "A";
        await ocr.ReadOnceAsync(reader.Id);
        Assert.Equal("A", Assert.Single((await ocr.ListAsync(saver)).Readers).LastText);

        // A user of the same group whose view no longer holds the camera: no text, no problem of the camera.
        var other = FakeCallContext.WithGroup(ocr.Context, [ocr.Outside.Id], "operator2", key);
        var row = Assert.Single((await ocr.ListAsync(other)).Readers);
        Assert.Empty(row.LastText);
        Assert.Null(row.LastTime);
        Assert.Equal(ReaderRules.Off, row.Problem);
    }

    /// <summary>Cuts every value as the same three bytes.</summary>
    private sealed class FixedCutter : IPictureCutter
    {
        public byte[]? Cut(ReadOnlyMemory<byte> jpeg, RelativeBox box) => [1, 2, 3];
    }

    /// <summary>A user whose role lacks the host's Live view.</summary>
    private sealed class WithoutLiveView(FakeCallContext inner) : ICallContext
    {
        public string UserName => inner.UserName;

        public string SessionId => inner.SessionId;

        public string DeviceScopeKey => inner.DeviceScopeKey;

        public bool HasAllDevices => inner.HasAllDevices;

        public Oadm.Sdk.Devices.IDeviceRepository Devices => inner.Devices;

        public Oadm.Sdk.Vapix.IVapixClientFactory Vapix => inner.Vapix;

        public Oadm.Sdk.Tasks.ITaskRunner Tasks => inner.Tasks;

        public Oadm.Sdk.Devices.IDeviceEventStreams? EventStreams => inner.EventStreams;

        public bool Has(string permission) => permission != HostPermissionKeys.DevicesLiveView && inner.Has(permission);

        public bool Contains(Oadm.Sdk.Devices.IDeviceInfo device) => inner.Contains(device);
    }
}
