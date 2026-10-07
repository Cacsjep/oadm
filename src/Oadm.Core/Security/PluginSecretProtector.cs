using System.Text;

using Oadm.Sdk.Plugins;

namespace Oadm.Core.Security;

/// <summary>
/// <see cref="ISecretProtector"/> for core plugins: AES-256-GCM with the server master key
/// (<see cref="CredentialProtector"/>), the purpose bound as associated data. Output is base64.
/// </summary>
public sealed class PluginSecretProtector(CredentialProtector protector) : ISecretProtector
{
    private const string Prefix = "oadm-plugin-secret:";

    public string Protect(string plaintext, string purpose)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return Convert.ToBase64String(protector.Protect(plaintext, Encoding.UTF8.GetBytes(Prefix + purpose)));
    }

    public string Unprotect(string protectedValue, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedValue);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return protector.Unprotect(Convert.FromBase64String(protectedValue), Encoding.UTF8.GetBytes(Prefix + purpose));
    }
}
