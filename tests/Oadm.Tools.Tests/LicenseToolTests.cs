using Oadm.Tools;
using Oadm.Sdk.Licensing;

namespace Oadm.Tools.Tests;

public sealed class LicenseToolTests : IDisposable
{
    private const string Server = "7f3c2a1e-5b8d-4c6f-9a2e-1d0b4e8f6c37";
    private static readonly DateOnly Today = new(2026, 10, 17);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "oadm-license-" + Guid.NewGuid().ToString("N"));

    public LicenseToolTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private (int Code, string Out, string Error) Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = LicenseCommands.Run(args, output, error, _folder, Today);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Keys_then_sign_make_a_license_the_server_accepts()
    {
        var keys = Run("keys", "--out", "keys");
        Assert.Equal(0, keys.Code);
        var publicKey = File.ReadAllText(Path.Combine(_folder, "keys", LicenseCommands.PublicKeyFile)).Trim();
        Assert.Contains($"\"publicKey\": \"{publicKey}\"", keys.Out, StringComparison.Ordinal);

        var sign = Run("sign", "--key", "keys/private-key.pem", "--plugin", "acme.site-reports", "--server", Server.ToUpperInvariant(),
            "--customer", "ACME GmbH", "--expires", "2027-12-31", "--features", "reports, export", "--limit", "sites=3");
        Assert.Equal(0, sign.Code);
        var file = Path.Combine(_folder, "acme-gmbh-acme.site-reports.oadmlicense");
        Assert.Contains(file, sign.Out, StringComparison.Ordinal);

        var info = LicenseFile.Verify(File.ReadAllText(file), publicKey, "acme.site-reports", Server, Today);
        Assert.Equal(LicenseStatus.Valid, info.Status);
        Assert.Equal("Licensed to ACME GmbH until 2027-12-31.", info.Reason);
        Assert.Equal(["reports", "export"], info.Data!.Features);
        Assert.Equal(3, info.Limit("sites"));
        Assert.True(info.Has("export"));
        Assert.False(info.Has("other"));
    }

    [Fact]
    public void Keys_never_overwrite_a_key()
    {
        Assert.Equal(0, Run("keys", "--out", "k").Code);
        var again = Run("keys", "--out", "k");
        Assert.Equal(1, again.Code);
        Assert.Contains("exists already", again.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sign", "--plugin", "a.b", "--server", Server, "--customer", "X")] // no key
    [InlineData("sign", "--key", "k.pem", "--plugin", "a.b", "--server", "not-an-id", "--customer", "X")]
    [InlineData("sign", "--key", "k.pem", "--plugin", "a.b", "--server", Server, "--customer", "X", "--expires", "31.12.2027")]
    [InlineData("sign", "--key", "k.pem", "--plugin", "a.b", "--server", Server, "--customer", "X", "--expires", "2020-01-01")]
    [InlineData("sign", "--key", "k.pem", "--plugin", "a.b", "--server", Server, "--customer", "X", "--limit", "sites")]
    [InlineData("sign", "--key", "k.pem", "--plugin", "a.b", "--server", Server, "--customer", "X", "--color", "red")]
    [InlineData("verify")]
    public void Wrong_arguments_are_usage_errors(params string[] args) => Assert.Equal(2, Run(args).Code);
}
