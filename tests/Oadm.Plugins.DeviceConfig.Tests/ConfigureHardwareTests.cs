using System.Net;

using Oadm.Core.Tests.Hardware;
using Oadm.Core.Vapix;
using Oadm.Plugins.DeviceConfig.Configure;

using Xunit.Abstractions;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>
/// Read-only: the read query of Configure devices against every dev camera (dev-cameras.yaml): parameter definitions per
/// group, action rules, schedules and stream profiles. Nothing is written.
/// </summary>
[Trait("Category", "Hardware")]
public sealed class ConfigureHardwareTests(ITestOutputHelper output)
{
    [HardwareFact]
    public async Task Every_dev_camera_answers_the_read_query()
    {
        foreach (var camera in DevCameras.All)
        {
            using var handler = new HttpClientHandler
            {
                Credentials = new NetworkCredential(camera.User, camera.Password),
                ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            };
            using var vapix = new VapixClient(new Uri($"{camera.EffectiveScheme}://{camera.Address}/"), handler, disposeHandler: false);
            DeviceSettingsReader.ClearCache();
            var settings = await DeviceSettingsReader.ReadAsync(new QueryContext(vapix), new FakeDevice { Address = camera.Address }, full: true, CancellationToken.None);

            output.WriteLine($"{camera.Address}: {settings.Model} {settings.Firmware}, {settings.Parameters.Count} parameters, "
                + $"{settings.Rules.Count} action rules, {settings.Schedules.Count} schedules, {settings.StreamProfiles.Count} stream profiles"
                + (settings.Problems.Count > 0 ? ", problems: " + string.Join("; ", settings.Problems) : string.Empty));
            Assert.Null(settings.Error);
            Assert.True(settings.Parameters.Count > 100, camera.Address);
            Assert.DoesNotContain(settings.Parameters, p => p.Name is "Brand.ProdNbr" or "Properties.Firmware.Version");
            Assert.All(settings.Parameters.Where(p => p.Kind == ParamKind.Password), p => Assert.Equal(string.Empty, p.Value));
        }
    }
}
