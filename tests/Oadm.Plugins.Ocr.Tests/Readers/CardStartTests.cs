using System.Diagnostics;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;
using Oadm.Plugins.Ocr.Recognition;
using Oadm.Plugins.Ocr.Tests.Recognition;
using Oadm.Sdk.Testing;

using Xunit.Abstractions;

namespace Oadm.Plugins.Ocr.Tests.Readers;

/// <summary>
/// The start with Use GPU on (docs/spec/ocr.md "GPU"): the plugin's start never waits for the graphics card's test, the readers
/// wait for it and then start where they stay, off never touches the card, a stop during the test ends cleanly. Fake
/// recognizer or a fake provider, no graphics card.
/// </summary>
public sealed class CardStartTests(ITestOutputHelper output)
{
    private static async Task<(OcrHarness Ocr, TaskCompletionSource Test, Reader Reader)> StartedWithAReaderAsync()
    {
        // A first start without a test stores the reader; the second one is the start under test.
        var ocr = await OcrHarness.StartAsync(onGpu: true);
        var reader = await ocr.SaveAsync(ocr.NewReader("Gate", ReaderMode.Watching));
        await Eventually.TrueAsync(() => ocr.Plugin.Runtime!.Watching.Count == 1, "the reader reads");
        await ocr.Plugin.StopAsync(CancellationToken.None);
        var test = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ocr.Recognizer.CardTest = test.Task;
        await ocr.Plugin.StartAsync(ocr.Context, CancellationToken.None);
        return (ocr, test, reader);
    }

    [Fact]
    public async Task The_readers_wait_for_the_test_and_the_page_says_Starting()
    {
        var (ocr, test, reader) = await StartedWithAReaderAsync();
        await using (ocr)
        {
            var runtime = ocr.Plugin.Runtime!;
            Assert.True(runtime.Starting);
            Assert.Empty(runtime.Watching);
            Assert.Equal(ReaderRules.Starting, Assert.Single((await ocr.ListAsync()).Readers).Problem);
            Assert.True(OcrJson.Deserialize<EngineInfo>(await ocr.CallAsync(OcrMethods.GetEngine)).GpuStarting);
            var stats = OcrJson.Deserialize<OcrStats>(await ocr.CallAsync(OcrMethods.GetStats));
            Assert.Equal((LoadState.Starting, ReaderRules.Starting), (stats.State, stats.StateText));
            Assert.Equal(0, ocr.Frames.Open);

            // A preview waits with them.
            var preview = await ocr.PreviewAsync(ocr.NewReader("Draft"));
            Assert.Equal(ReaderRules.Starting, preview.Problem);
            Assert.Equal(0, ocr.Frames.Open);

            test.SetResult();

            await Eventually.TrueAsync(() => runtime.Watching.Contains(reader.Id), "the reader started");
            Assert.False(runtime.Starting);
            await Eventually.TrueAsync(async () => Assert.Single((await ocr.ListAsync()).Readers).Problem is null, "the chip went");
            Assert.False(OcrJson.Deserialize<EngineInfo>(await ocr.CallAsync(OcrMethods.GetEngine)).GpuStarting);
            await Eventually.TrueAsync(() => ocr.Context.PublishedEvents.Any(e => e.Topic == OcrMethods.ReadersTopic && e.PayloadJson?.Contains(ReaderRules.Starting, StringComparison.Ordinal) == false), "the pages heard");
            Assert.Null((await ocr.PreviewAsync(ocr.NewReader("Draft"))).Problem);
        }
    }

    [Fact]
    public async Task Read_text_waits_for_the_test()
    {
        var (ocr, test, _) = await StartedWithAReaderAsync();
        await using (ocr)
        {
            var onAction = await ocr.SaveAsync(ocr.NewReader("Gate 2", ReaderMode.OnAction));
            ocr.Frames.Single = "G3";
            var read = ocr.Plugin.ReadOnceAsync(onAction, CancellationToken.None);
            await Eventually.StaysTrueAsync(() => !read.IsCompleted && ocr.Recognizer.Started == 0, "the action waits", TimeSpan.FromMilliseconds(200));

            test.SetResult();

            Assert.Equal("G3", (await read.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System)).Reading.Areas[0].Text);
        }
    }

    [Fact]
    public async Task A_test_that_fails_starts_the_readers_all_the_same()
    {
        var (ocr, test, reader) = await StartedWithAReaderAsync();
        await using (ocr)
        {
            test.SetException(new InvalidOperationException("The driver went."));

            await Eventually.TrueAsync(() => ocr.Plugin.Runtime!.Watching.Contains(reader.Id), "the reader started");
        }
    }

    [Fact]
    public async Task A_plugin_that_stops_during_the_test_ends_cleanly()
    {
        var (ocr, test, _) = await StartedWithAReaderAsync();
        await using (ocr)
        {
            await ocr.Plugin.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System);
            var events = ocr.Context.PublishedEvents.Count;

            test.SetResult();

            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => ocr.CallAsync(OcrMethods.GetEngine));
            Assert.Equal(events, ocr.Context.PublishedEvents.Count);
            Assert.Equal(0, ocr.Frames.Open);
        }
    }

    [Fact]
    public async Task With_Use_GPU_off_nothing_waits_and_the_card_is_never_tried()
    {
        var tries = 0;
        await using var recognizer = new OnnxTextRecognizer(new OcrEngineOptions
        {
            ModelDirectory = RecognizerFixture.ModelFolder,
            UseGraphicsCard = false,
            ProcessorCount = 2,
            FindGraphicsCard = (_, _) =>
            {
                Interlocked.Increment(ref tries);
                return null;
            },
        });
        await using var ocr = new OcrHarness(recognizer: recognizer);
        ocr.Context.PluginSettings.Values[OcrPlugin.UseGpuSetting] = "false";

        await ocr.Plugin.StartAsync(ocr.Context, CancellationToken.None);

        Assert.False(ocr.Plugin.Runtime!.Starting);
        Assert.False(OcrJson.Deserialize<EngineInfo>(await ocr.CallAsync(OcrMethods.GetEngine)).GpuStarting);
        await recognizer.CardTry;
        Assert.Equal(0, tries);
    }

    [Fact]
    public async Task The_setting_stays_the_callers_while_the_test_runs()
    {
        // setUseGpu keeps answering after the card's try, and off takes effect at once.
        await using var ocr = await OcrHarness.StartAsync(onGpu: true);
        var test = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ocr.Recognizer.CardTest = test.Task;

        var on = ocr.CallAsync(OcrMethods.SetUseGpu, new UseGpuRequest { UseGpu = true });
        await Eventually.TrueAsync(() => ocr.Recognizer.SetUseGpuCalls >= 2, "the setting handed over");
        Assert.False(on.IsCompleted);

        test.SetResult();
        Assert.True(OcrJson.Deserialize<EngineInfo>(await on).UseGpu);
    }

    [Fact]
    public async Task A_test_past_its_limit_gives_up_and_a_late_end_frees_what_it_made()
    {
        var time = new FakeTimeProvider();
        using var hold = new SemaphoreSlim(0);
        using var entered = new SemaphoreSlim(0);
        var logger = new RecognizerTests.ListLogger();
        await using var recognizer = new OnnxTextRecognizer(
            new OcrEngineOptions
            {
                ModelDirectory = RecognizerFixture.ModelFolder,
                UseGraphicsCard = false,
                ProcessorCount = 2,
                Time = time,
                FindGraphicsCard = (_, _) =>
                {
                    entered.Release();
                    hold.Wait();
                    return null;
                },
            },
            logger);

        var start = recognizer.SetUseGpu(true);
        await entered.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(start.IsCompleted);

        time.Advance(OnnxTextRecognizer.CardStartLimit);

        await start.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System);
        Assert.Contains(logger.Lines, l => l.StartsWith("Text recognition runs on the CPU", StringComparison.Ordinal));
        Assert.False(recognizer.GpuAvailable);
        await recognizer.SetUseGpu(true); // never a second try while the first one hangs
        hold.Release();
    }

    [Fact]
    public async Task A_recognizer_disposed_during_the_test_frees_the_card_when_the_test_ends()
    {
        using var hold = new SemaphoreSlim(0);
        using var entered = new SemaphoreSlim(0);
        var recognizer = new OnnxTextRecognizer(new OcrEngineOptions
        {
            ModelDirectory = RecognizerFixture.ModelFolder,
            UseGraphicsCard = false,
            ProcessorCount = 2,
            FindGraphicsCard = (_, _) =>
            {
                entered.Release();
                hold.Wait();
                return ExecutionTarget.Cpu; // the whole way with real models on the CPU: no card in a test
            },
        });
        var start = recognizer.SetUseGpu(true);
        await entered.WaitAsync(TimeSpan.FromSeconds(30));

        await recognizer.DisposeAsync();
        hold.Release();

        await start.WaitAsync(TimeSpan.FromMinutes(1), TimeProvider.System);
        Assert.False(recognizer.GpuAvailable);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task What_the_start_costs_with_the_models_on_the_cpu()
    {
        var logger = new RecognizerTests.ListLogger();
        var watch = Stopwatch.StartNew();
        await using var recognizer = new OnnxTextRecognizer(
            new OcrEngineOptions
            {
                ModelDirectory = RecognizerFixture.ModelFolder,
                UseGraphicsCard = false,
                FindGraphicsCard = (_, _) => Environment.GetEnvironmentVariable("OADM_SPEED_CARD") == "1" ? SessionFactory.GraphicsCardTarget() : ExecutionTarget.Cpu,
            },
            logger);
        output.WriteLine($"Recognizer: {watch.ElapsedMilliseconds} ms");
        await recognizer.SetUseGpu(true);
        foreach (var line in logger.Lines)
        {
            output.WriteLine(line);
        }

        Assert.Contains(logger.Lines, l => l.Contains("test took", StringComparison.Ordinal));
    }
}
