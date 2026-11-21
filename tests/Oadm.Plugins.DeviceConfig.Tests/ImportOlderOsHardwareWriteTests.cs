using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Oadm.Core.Tests.Hardware;
using Oadm.Core.Vapix;
using Oadm.Sdk.Vapix;
using StepRun = Oadm.Sdk.Testing.StepRun;

using Xunit.Abstractions;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>
/// Opt-in write test of Import configuration's way for AXIS OS below 12.5, with the real task and the real VAPIX client, only
/// with <c>OADM_CONFIG_OLDER_WRITE=&lt;address&gt;</c> of a dev camera (dev-cameras.yaml; the user allowed 10.0.0.12 and
/// 10.0.0.202). The camera's own AXIS OS is reported as 11.9.65 to the task (a camera of 12.5 or later takes the old way
/// too), so the parameters go through param.cgi. It sets the name of view area 1 (harmless text), read first, and sets it
/// back in a finally block; the event rules area of the file is skipped with the warning and nothing else is sent.
/// </summary>
[Trait("Category", "HardwareWrite")]
public sealed class ImportOlderOsHardwareWriteTests(ITestOutputHelper output)
{
    private const string Parameter = "Image.I0.Name";
    private const string TestName = "OADM test";
    private static readonly string? Address = Environment.GetEnvironmentVariable("OADM_CONFIG_OLDER_WRITE");

    public sealed class WriteFactAttribute : FactAttribute
    {
        public WriteFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Address) || !DevCameras.All.Any(c => c.Address == Address))
            {
                Skip = "Set OADM_CONFIG_OLDER_WRITE to the address of a dev camera to write to it.";
            }
        }
    }

    /// <summary>The real client; basicdeviceinfo answers an AXIS OS below 12.5.</summary>
    private sealed class OlderOs(IVapixClient inner) : IVapixClient
    {
        public Uri BaseAddress => inner.BaseAddress;

        public async Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) =>
            (await inner.GetBasicDeviceInfoAsync(ct)) with { Version = "11.9.65" };

        public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => inner.ListParametersAsync(groups, ct);

        public Task RestartAsync(CancellationToken ct) => throw new InvalidOperationException("never in this test");

        public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct) => inner.GetApiListAsync(ct);

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Only param.cgi: the configuration API is never asked on this way.
            Assert.StartsWith("/axis-cgi/param.cgi", request.RequestUri!.OriginalString, StringComparison.Ordinal);
            return inner.SendAsync(request, ct);
        }
    }

    [WriteFact]
    public async Task The_parameters_of_a_file_are_set_through_param_cgi_and_set_back()
    {
        var camera = DevCameras.All.First(c => c.Address == Address);
        using var handler = new HttpClientHandler
        {
            Credentials = new NetworkCredential(camera.User, camera.Password),
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var real = new VapixClient(new Uri($"{camera.EffectiveScheme}://{camera.Address}/"), handler, disposeHandler: false);
        var vapix = new OlderOs(real);
        var ct = CancellationToken.None;
        var before = (await real.ListParametersAsync([Parameter], ct))[Parameter];
        var info = await vapix.GetBasicDeviceInfoAsync(ct);
        output.WriteLine($"{camera.Address}: {info.ProdNbr}, {Parameter}={before}");
        Assert.NotEqual(TestName, before);

        var file = new JsonObject
        {
            ["status"] = "success",
            ["data"] = new JsonObject
            {
                ["param.v2"] = new JsonObject { ["ImageCollection"] = new JsonArray(new JsonObject { ["groupId"] = "I0", ["Name"] = TestName }) },
                ["action-rules.v2"] = new JsonObject { ["recipients"] = new JsonArray(), ["rules"] = new JsonArray() },
            },
            ["metadata"] = new JsonObject { ["prodNbr"] = info.ProdNbr, ["fwVersion"] = info.Version },
        }.ToJsonString();
        var device = new FakeDevice { Model = "AXIS " + info.ProdNbr, FirmwareVersion = info.Version, Serial = info.SerialNumber, Address = camera.Address };
        var uploads = new MemoryUploads();
        var payload = new ImportPayload
        {
            FileId = uploads.Add("older.json", Encoding.UTF8.GetBytes(file)),
            FileName = "older.json",
            Sections = [ConfigSections.Param, ConfigSections.ActionRules],
            Files = new Dictionary<Guid, string> { [device.Id] = string.Empty },
        }.ToJson();

        try
        {
            var ctx = new RecordingContext(vapix, uploads);
            await StepRun.RunAsync(ctx.Steps, () => new ImportConfigTask().ExecuteAsync(ctx, device, payload, ct));
            foreach (var line in StepRun.Lines(ctx.Steps))
            {
                output.WriteLine(line);
            }

            Assert.Equal("1 value", StepRun.Detail(ctx.Steps, "Import network and system settings"));
            Assert.Equal(ImportConfigTask.OlderOsReason, StepRun.Detail(ctx.Steps, "Import event rules"));
            Assert.Equal("All imported values match", StepRun.Detail(ctx.Steps, "Verify configuration"));
            Assert.Equal("Event rules were not applied: this needs AXIS OS 12.5 or later.", Assert.Single(ctx.Warnings));
            Assert.Equal(TestName, (await real.ListParametersAsync([Parameter], ct))[Parameter]);
        }
        finally
        {
            await Configure.ParamWriter.UpdateAsync(real, [new NameValue(Parameter, before)], [], ct);
        }

        var after = (await real.ListParametersAsync([Parameter], ct))[Parameter];
        output.WriteLine($"Set back: {Parameter}={after}");
        Assert.Equal(before, after);
    }
}
