using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.DeviceClocks;
using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Certificates;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>The clock check before a certificate goes onto a camera (docs/spec/pki.md "Clock check").</summary>
public sealed class ClockTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static TimeSpan Span(int minutes, int seconds) => new(0, minutes, seconds);

    [Fact]
    public void The_question_says_how_far_behind_and_when_the_certificate_becomes_valid()
    {
        Assert.Equal("The camera's time is 9 min 12 s behind. The certificate becomes valid on the camera only in 4 min 12 s. Set the camera to use NTP. Continue?",
            CameraClock.Question([Span(9, 12)], 1));
        Assert.Equal("The time of 2 of 5 cameras is behind, by up to 9 min 12 s. The certificates become valid on them only in up to 4 min 12 s. Set the cameras to use NTP. Continue?",
            CameraClock.Question([Span(6, 0), Span(9, 12), Span(1, 0), -Span(30, 0)], 5));

        // Up to 5 s behind the certificate's start (5 min back), and any camera ahead: no question.
        Assert.Null(CameraClock.Question([Span(5, 5), TimeSpan.Zero, -TimeSpan.FromHours(3)], 3));
        Assert.True(CameraClock.NeedsQuestion(Span(5, 6)));
        Assert.Null(CameraClock.Question([], 3));

        Assert.Equal(("2 h 3 min", "45 s", "7 min", "1 h"), (CameraClock.Describe(TimeSpan.FromMinutes(123)), CameraClock.Describe(TimeSpan.FromSeconds(45)),
            CameraClock.Describe(TimeSpan.FromMinutes(7)), CameraClock.Describe(TimeSpan.FromHours(1))));
    }

    [Fact]
    public async Task The_answer_goes_into_the_payload()
    {
        var asked = new List<string>();
        Assert.Equal("{}", await ClockCheck.PayloadAsync([Span(1, 0)], 1, q => { asked.Add(q); return Task.FromResult(true); }));
        Assert.Empty(asked);
        var yes = await ClockCheck.PayloadAsync([Span(9, 12)], 1, q => { asked.Add(q); return Task.FromResult(true); });
        Assert.True(CameraClock.Confirmed(yes));
        Assert.Null(await ClockCheck.PayloadAsync([Span(9, 12)], 1, _ => Task.FromResult(false)));
        Assert.Single(asked);
        Assert.False(CameraClock.Confirmed("{}"));
        Assert.False(CameraClock.Confirmed("not json"));
        Assert.False(CameraClock.Confirmed(null));
    }

    [Fact]
    public async Task Reading_the_clocks_runs_eight_at_a_time_and_leaves_out_cameras_that_do_not_answer()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToList();
        var silent = ids[3];
        var gate = new TaskCompletionSource();
        var running = 0;
        var most = 0;
        var started = 0;
        var server = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        async Task<string?> Query(Guid id, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            Interlocked.Increment(ref started);
            try
            {
                if (id == silent)
                {
                    await Task.Delay(Timeout.Infinite, ct); // never answers
                }

                await gate.Task.WaitAsync(ct);
                return id == ids[0] ? "garbage" : JsonSerializer.Serialize(new ClockReading(server - Span(9, 12), server), Json);
            }
            finally
            {
                Interlocked.Decrement(ref running);
            }
        }

        var done = new List<int>();
        var read = CameraClock.ReadBehindAsync(ids, Query, new SyncProgress(done), time, CancellationToken.None);
        await Eventually.TrueAsync(() => Volatile.Read(ref started) == CameraClock.MaxParallel, "eight reads started");
        Assert.Equal(CameraClock.MaxParallel, Volatile.Read(ref most));

        gate.SetResult();
        await Eventually.TrueAsync(() => Volatile.Read(ref started) == ids.Count, "every read started");
        time.Advance(CameraClock.ReadTimeout); // the silent camera's wait ends
        var behind = await read;

        Assert.Equal(ids.Count - 2, behind.Count); // the unreadable answer and the silent camera are left out
        Assert.All(behind, b => Assert.Equal(Span(9, 12), b));
        Assert.True(Volatile.Read(ref most) <= CameraClock.MaxParallel);
        lock (done)
        {
            Assert.Equal(ids.Count, done.Max());
        }
    }

    [Fact]
    public async Task A_dialog_reads_no_more_than_the_first_hundred_of_30000_cameras()
    {
        var ids = Enumerable.Range(0, 30_000).Select(_ => Guid.NewGuid()).ToList();
        var asked = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var server = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        Task<string?> Query(Guid id, CancellationToken ct)
        {
            asked.Add(id);
            return Task.FromResult<string?>(JsonSerializer.Serialize(new ClockReading(server - Span(9, 0), server), Json));
        }

        var behind = await CameraClock.ReadBehindAsync(ids, Query, null, new FakeTimeProvider(server), CancellationToken.None);

        Assert.Equal(CameraClock.MaxRead, asked.Count);
        Assert.Equal(ids.Take(CameraClock.MaxRead).ToHashSet(), asked.ToHashSet());
        Assert.Equal(CameraClock.MaxRead, behind.Count);
        Assert.Equal(CameraClock.MaxRead, CameraClock.Sampled(ids.Count));
        Assert.Equal(3, CameraClock.Sampled(3));
    }

    [Fact]
    public async Task Device_certificates_of_the_oadm_ca_start_five_minutes_in_the_past()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        await using var pki = await PkiHarness.StartAsync(time: time);
        time.Advance(TimeSpan.FromHours(1)); // past the CA's own start
        using var key = RSA.Create(2048);
        var csr = new CertificateRequest("CN=camera", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequest();

        var issued = await new PkiCertificateAuthority(pki.Service).SignAsync(
            new DeviceCertificateRequest(csr, "10.0.0.48", ["10.0.0.48"], ["axis-b8a44f631339.local"], DeviceCertificateUse.Client) { Source = "EST" },
            CancellationToken.None);

        using var certificate = X509CertificateLoader.LoadCertificate(issued.Certificate);
        Assert.Equal(time.GetUtcNow().UtcDateTime.AddMinutes(-5), certificate.NotBefore.ToUniversalTime());
    }

    [Fact]
    public async Task Enable_8021x_refuses_a_camera_behind_unless_the_user_continued_and_a_camera_ahead_is_fine()
    {
        await using var h = await TaskHarness.StartAsync();
        h.Camera.ClockOffset = -Span(12, 30);

        var ex = await h.RunAsync(PkiTaskIds.Dot1xEnable);
        // The fake camera's clock follows the real one: under load the task reads it seconds later, so no exact minute.
        Assert.StartsWith("The device's time is ", ex!.Message, StringComparison.Ordinal);
        Assert.Matches(@"is 1[12] min \d+ s behind\. The certificate becomes valid on the device only in [67] min \d+ s\.", ex.Message);
        Assert.EndsWith(" Set the device to use NTP. Nothing was changed.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Check device clock: Failed", h.Lines);
        Assert.Empty(h.Camera.Writes);

        // The user said Yes in the dialog: it goes on and notes it.
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable, CameraClock.Payload(confirmed: true)));
        Assert.Contains(h.Context.LogEntries, e => e.Level == TaskLogLevel.Warning && e.Message.Contains("The user chose to continue.", StringComparison.Ordinal));

        // Ahead (it was refused at more than 5 minutes before): fine without a question.
        h.Camera.ClockOffset = TimeSpan.FromMinutes(12);
        Assert.Null(await h.RunAsync(PkiTaskIds.Dot1xEnable));
    }

    [Fact]
    public async Task Enable_https_refuses_a_camera_behind_unless_the_user_continued()
    {
        await using var h = await TaskHarness.StartAsync();
        h.Camera.ClockOffset = -Span(12, 30);

        var ex = await h.RunAsync(PkiTaskIds.HttpsEnable);
        Assert.EndsWith(" Set the device to use NTP. Nothing was changed.", ex!.Message, StringComparison.Ordinal);
        Assert.Contains("Check device clock: Failed", h.Lines);
        Assert.Empty(h.Camera.Writes);

        Assert.Null(await h.RunAsync(PkiTaskIds.HttpsEnable, CameraClock.Payload(confirmed: true)));
        Assert.Contains(h.Context.LogEntries, e => e.Level == TaskLogLevel.Warning && e.Message.Contains("The user chose to continue.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_dialogs_read_the_clock_through_a_read_only_query()
    {
        await using var h = await TaskHarness.StartAsync();
        h.Camera.ClockOffset = -Span(9, 0);
        var https = h.Task<HttpsEnableTask>(PkiTaskIds.HttpsEnable);
        Assert.True(https.RequiresDialog);
        var query = new PkiTaskContext(h.Camera);

        foreach (ITaskPluginQuery task in new ITaskPluginQuery[] { https, h.Task<Dot1xEnableTask>(PkiTaskIds.Dot1xEnable) })
        {
            var reading = JsonSerializer.Deserialize<ClockReading>((await task.QueryAsync(query, h.Device, CameraClock.QueryMethod, null, CancellationToken.None))!, Json)!;
            var behind = CameraClock.Behind(reading)!.Value;
            Assert.InRange(behind, Span(8, 30), Span(9, 30)); // the camera's clock against the server's
        }

        await Assert.ThrowsAsync<ArgumentException>(() => https.QueryAsync(query, h.Device, "other", null, CancellationToken.None));
        Assert.Empty(h.Camera.Writes);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private sealed class SyncProgress(List<int> done) : IProgress<int>
    {
        public void Report(int value)
        {
            lock (done)
            {
                done.Add(value);
            }
        }
    }
}
