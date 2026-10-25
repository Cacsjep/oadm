using System.Net;

using Oadm.Plugins.Pki.Device;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>SOAP answers: an HTTP error is never a success, also without a fault element.</summary>
public sealed class DeviceSoapErrorTests
{
    [Fact]
    public async Task A_soap_http_error_without_a_fault_element_is_an_error()
    {
        var camera = new FakeCamera
        {
            SoapAnswer = (HttpStatusCode.InternalServerError, DeviceSoap.Envelope("<aweb:GetWebServerTlsConfigurationResponse><aweb:Note>NoFault</aweb:Note></aweb:GetWebServerTlsConfigurationResponse>")),
        };

        var ex = await Assert.ThrowsAsync<PkiDeviceException>(() => DeviceSoap.SendAsync(camera, "<aweb:GetWebServerTlsConfiguration/>", "Read web server settings", CancellationToken.None));

        Assert.Equal(500, ex.HttpStatus);
        Assert.StartsWith("Read web server settings: ", ex.Message, StringComparison.Ordinal);

        // A SOAP fault still says the device's reason.
        camera.SoapAnswer = (HttpStatusCode.InternalServerError, PkiFixture.Read(PkiFixture.Fault));
        ex = await Assert.ThrowsAsync<PkiDeviceException>(() => DeviceSoap.SendAsync(camera, "<aweb:GetWebServerTlsConfiguration/>", "Read web server settings", CancellationToken.None));
        Assert.Contains("the device refused", ex.Message, StringComparison.Ordinal);
    }
}
