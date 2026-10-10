using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Oadm.Core.Vapix;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>
/// Opt-in write test: imports a camera's own export back into it with the real task and the real VAPIX client, then checks
/// that nothing changed. Runs only with <c>OADM_CONFIG_WRITE_URL</c> (e.g. <c>http://10.0.0.48</c>),
/// <c>OADM_CONFIG_WRITE_USER</c> and <c>OADM_CONFIG_WRITE_PASSWORD</c> set, and only when the developer asks for it
/// (CLAUDE.md "device safety" 6). Passed on AXIS Q3546-LVE and AXIS M3098-LV, AXIS OS 12.11.118 (2026-10-10).
/// </summary>
[Trait("Category", "HardwareWrite")]
public sealed class ImportHardwareWriteTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("OADM_CONFIG_WRITE_URL");

    public sealed class WriteFactAttribute : FactAttribute
    {
        public WriteFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Url))
            {
                Skip = "OADM_CONFIG_WRITE_URL is not set.";
            }
        }
    }

    [WriteFact]
    public async Task Importing_the_own_export_changes_nothing()
    {
        using var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(Environment.GetEnvironmentVariable("OADM_CONFIG_WRITE_USER"), Environment.GetEnvironmentVariable("OADM_CONFIG_WRITE_PASSWORD")),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var vapix = new VapixClient(new Uri(Url!.TrimEnd('/') + "/"), handler, disposeHandler: false);
        var before = await ConfigApi.ExportAsync(vapix, CancellationToken.None);
        var file = ConfigFile.Parse(before);
        var info = await vapix.GetBasicDeviceInfoAsync(CancellationToken.None);

        var device = new FakeDevice { Model = "AXIS " + info.ProdNbr, FirmwareVersion = info.Version, Serial = info.SerialNumber };
        var uploads = new MemoryUploads();
        var payload = new ImportPayload
        {
            FileId = uploads.Add("own.json", Encoding.UTF8.GetBytes(before)),
            FileName = "own.json",
            Sections = [.. file.Sections.Select(s => s.Key)],
            Files = new Dictionary<Guid, string> { [device.Id] = string.Empty },
        }.ToJson();
        var ctx = new RecordingContext(vapix, uploads);
        await StepRun.RunAsync(ctx.Steps, () => new ImportConfigTask().ExecuteAsync(ctx, device, payload, CancellationToken.None));

        Assert.All(StepRun.Lines(ctx.Steps), line => Assert.EndsWith(": Done", line, StringComparison.Ordinal));
        Assert.Empty(ctx.Warnings);
        var after = (JsonObject)JsonNode.Parse(await ConfigApi.ExportAsync(vapix, CancellationToken.None))!["data"]!;
        Assert.Empty(ConfigCompare.Differences(file.Data, after));
    }
}
