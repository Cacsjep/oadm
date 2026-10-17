using System.Net;

using Oadm.Core.Vapix;
using Oadm.Plugins.EdgeStorage.Device;
using Oadm.Plugins.EdgeStorage.Disks;
using StepRun = Oadm.Sdk.Testing.StepRun;

namespace Oadm.Plugins.EdgeStorage.Tests;

/// <summary>
/// Opt-in write test: runs every SD card tool with the real task and the real VAPIX client on one camera, then checks that
/// the card ends as it started (mounted, retention, not encrypted). FORMATS THE CARD: every recording on it is deleted. Runs
/// only with <c>OADM_STORAGE_WRITE_URL</c> (e.g. <c>http://10.0.0.48</c>), <c>OADM_STORAGE_WRITE_USER</c> and
/// <c>OADM_STORAGE_WRITE_PASSWORD</c> set, and only when the developer asks for it (CLAUDE.md "device safety" 6).
/// </summary>
[Trait("Category", "HardwareWrite")]
public sealed class StorageHardwareWriteTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("OADM_STORAGE_WRITE_URL");

    public sealed class WriteFactAttribute : FactAttribute
    {
        public WriteFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Url))
            {
                Skip = "OADM_STORAGE_WRITE_URL is not set.";
            }
        }
    }

    [WriteFact]
    public async Task Every_tool_runs_and_the_card_ends_as_it_started()
    {
        using var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(Environment.GetEnvironmentVariable("OADM_STORAGE_WRITE_USER"), Environment.GetEnvironmentVariable("OADM_STORAGE_WRITE_PASSWORD")),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var vapix = new VapixClient(new Uri(Url!.TrimEnd('/') + "/"), handler, disposeHandler: false);
        var client = new StorageClient(vapix, TimeProvider.System);
        var before = await client.GetSdCardAsync(CancellationToken.None);
        Assert.True(before.IsConnected, "The SD card must be in use before the test.");
        Assert.False(before.Encrypted == true, "The test turns encryption on and off; start with an unencrypted card.");
        var device = new FakeDevice { Address = new Uri(Url!).Authority };
        var otherDays = before.CleanupMaxAgeDays == 30 ? 31 : 30;

        foreach (var payload in new[]
        {
            new TaskPayload(StorageActions.Retention, RetentionDays: otherDays),
            new TaskPayload(StorageActions.Retention, RetentionDays: before.CleanupMaxAgeDays),
            new TaskPayload(StorageActions.Check),
            new TaskPayload(StorageActions.Repair),
            new TaskPayload(StorageActions.Format),
            new TaskPayload(StorageActions.EncryptionOn, Passphrase: "OADM-hardware-test-2026"),
            new TaskPayload(StorageActions.EncryptionOff),
        })
        {
            var ctx = new RecordingContext(vapix);
            await StepRun.RunAsync(ctx.Steps, () => new StorageTaskPlugin(null).ExecuteAsync(ctx, device, EdgeStorageJson.Serialize(payload), CancellationToken.None));
            Assert.All(StepRun.Lines(ctx.Steps), line => Assert.Matches(": (Done|Skipped)$", line));
            var card = await client.GetSdCardAsync(CancellationToken.None);
            Assert.True(card.IsConnected, payload.Action + ": the card is in use again");
            Assert.Equal(payload.Action == StorageActions.EncryptionOn, card.Encrypted == true);
        }

        var after = await client.GetSdCardAsync(CancellationToken.None);
        Assert.Equal((before.CleanupMaxAgeDays, before.EncryptionEnabled, before.Encrypted), (after.CleanupMaxAgeDays, after.EncryptionEnabled, after.Encrypted));
    }
}
