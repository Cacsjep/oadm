using System.Security.Cryptography;
using System.Text;
using Oadm.Core.Security;

namespace Oadm.Core.Tests.Security;

public class CredentialProtectorTests
{
    private static CredentialProtector NewProtector() => new(RandomNumberGenerator.GetBytes(32));

    [Theory]
    [InlineData("")]
    [InlineData("pass")]
    [InlineData("A much longer passw0rd with spaces and symbols !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~")]
    [InlineData("unicode-äöü-€")]
    public void RoundTrip(string password)
    {
        using var protector = NewProtector();

        var blob = protector.Protect(password);

        Assert.Equal(CredentialProtector.NonceSize + Encoding.UTF8.GetByteCount(password) + CredentialProtector.TagSize, blob.Length);
        Assert.Equal(password, protector.Unprotect(blob));
    }

    [Fact]
    public void NonceIsRandomPerCall()
    {
        using var protector = NewProtector();

        var a = protector.Protect("same");
        var b = protector.Protect("same");

        Assert.NotEqual(a.AsSpan(0, CredentialProtector.NonceSize).ToArray(), b.AsSpan(0, CredentialProtector.NonceSize).ToArray());
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(0)]   // nonce
    [InlineData(14)]  // ciphertext
    [InlineData(-1)]  // tag (last byte)
    public void TamperingIsDetected(int index)
    {
        using var protector = NewProtector();
        var blob = protector.Protect("secret");

        var i = index < 0 ? blob.Length + index : index;
        blob[i] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(blob));
    }

    [Fact]
    public void TruncatedBlobIsRejected()
    {
        using var protector = NewProtector();
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(new byte[10]));
    }

    [Fact]
    public void WrongKeyFails()
    {
        using var a = NewProtector();
        using var b = NewProtector();

        var blob = a.Protect("secret");

        Assert.ThrowsAny<CryptographicException>(() => b.Unprotect(blob));
    }

    [Fact]
    public void AssociatedDataMustMatch()
    {
        using var protector = NewProtector();
        var deviceA = Guid.NewGuid().ToByteArray();
        var deviceB = Guid.NewGuid().ToByteArray();

        var blob = protector.Protect("secret", deviceA);

        Assert.Equal("secret", protector.Unprotect(blob, deviceA));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(blob, deviceB));
    }

    [Fact]
    public void KeyMustBe32Bytes()
    {
        Assert.Throws<ArgumentException>(() => new CredentialProtector(new byte[16]));
    }

    [Fact]
    public void ToStringRevealsNothing()
    {
        using var protector = NewProtector();
        Assert.Equal(nameof(CredentialProtector), protector.ToString());
    }
}
