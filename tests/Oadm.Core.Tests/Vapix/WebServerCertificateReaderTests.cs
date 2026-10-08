using Oadm.Core.Vapix;

namespace Oadm.Core.Tests.Vapix;

/// <summary>Parsing of the two read-only answers used to read a web server certificate over HTTP (10.0.0.48, AXIS OS 12.11).</summary>
public sealed class WebServerCertificateReaderTests
{
    private const string Answer =
        """<?xml version="1.0" encoding="UTF-8"?><SOAP-ENV:Envelope xmlns:SOAP-ENV="http://www.w3.org/2003/05/soap-envelope" xmlns:aweb="http://www.axis.com/vapix/ws/webserver" xmlns:acert="http://www.axis.com/vapix/ws/cert"><SOAP-ENV:Body><aweb:GetWebServerTlsConfigurationResponse><aweb:Configuration name="WebServer"><aweb:Tls>true</aweb:Tls><aweb:CertificateSet><acert:Certificates><acert:Id>Trustlix Device HTTPS lUZuIXuu</acert:Id></acert:Certificates><acert:CACertificates><acert:Id>Issuing CA</acert:Id></acert:CACertificates><acert:TrustedCertificates/></aweb:CertificateSet></aweb:Configuration></aweb:GetWebServerTlsConfigurationResponse></SOAP-ENV:Body></SOAP-ENV:Envelope>""";

    [Fact]
    public void AliasesComeFromTheCertificateSet()
    {
        var (alias, caAliases) = WebServerCertificateReader.ParseAliases(Answer);

        Assert.Equal("Trustlix Device HTTPS lUZuIXuu", alias);
        Assert.Equal(["Issuing CA"], caAliases);
    }

    [Fact]
    public void AnswerWithoutConfigurationHasNoAlias()
    {
        var (alias, caAliases) = WebServerCertificateReader.ParseAliases(
            """<SOAP-ENV:Envelope xmlns:SOAP-ENV="http://www.w3.org/2003/05/soap-envelope"><SOAP-ENV:Body/></SOAP-ENV:Envelope>""");

        Assert.Null(alias);
        Assert.Empty(caAliases);
    }

    [Fact]
    public void PemComesFromTheData()
    {
        Assert.Equal(
            "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n",
            WebServerCertificateReader.ParsePem("""{"status":"success","data":{"alias":"a","certificate":"-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n","keystore":"SE0"}}"""));
        Assert.Null(WebServerCertificateReader.ParsePem("""{"status":"error","error":{"code":404,"message":"not found"}}"""));
    }
}
