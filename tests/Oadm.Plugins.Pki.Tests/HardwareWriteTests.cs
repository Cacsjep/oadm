namespace Oadm.Plugins.Pki.Tests;

/// <summary>Runs only when OADM_PKI_HARDWARE_WRITE=1 is set explicitly; never part of unit, perf or hardware runs.</summary>
public sealed class HardwareWriteFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "OADM_PKI_HARDWARE_WRITE";

    public HardwareWriteFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Device writes: set {EnvironmentVariable}=1 only after the user approved a write test on a named device.";
        }
    }
}

/// <summary>
/// Skeleton of the opt-in hardware write tests of the PKI tasks (HARD RULE: write tests only on explicit request). The open
/// points from docs/specs/pki.md that only a real device answers:
/// 1. POST /config/rest/cert/v1/create_certificate (RSA-2048, default keystore SE0) + get_csr + PATCH certificates/&lt;alias&gt;
///    with the OADM-signed PEM (also: does PATCH accept a PEM chain?), POST ca_certificates.
/// 2. SOAP aweb:SetWebServerTlsConfiguration with the new alias (policy unchanged), the TLS handshake then presents it; and
///    the policy value "Http" for "HTTPS: Disable" (inferred, no schema published), back to "HttpAndHttps".
/// 3. network_settings.cgi setWired8021XConfiguration enabled=true / EAP-TLS / certClient / certsCA, read back, then
///    enabled=false (on a port without 802.1X enforcement only).
/// 4. install_from_pkcs12, DELETE certificates/&lt;alias&gt; and ca_certificates/&lt;alias&gt;.
/// Each test must restore the device (web server certificate and policy, 802.1X off, OADM certificates removed).
/// </summary>
[Trait("Category", "HardwareWrite")]
public sealed class HardwareWriteTests
{
    [HardwareWriteFact]
    public void Https_enable_update_and_disable_on_a_real_device() =>
        Assert.Fail("Not written yet: needs the user's approval for device writes on a named device (see the class summary).");

    [HardwareWriteFact]
    public void Dot1x_enable_and_disable_on_a_real_device() =>
        Assert.Fail("Not written yet: needs the user's approval for device writes on a named device (see the class summary).");
}
