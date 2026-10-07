namespace Oadm.Core.Security;

/// <summary>Stored credentials of one device (row of the DeviceCredentials table).</summary>
public sealed class DeviceCredential
{
    public Guid DeviceId { get; set; }

    public string UserName { get; set; } = string.Empty;

    /// <summary>Output of <see cref="CredentialProtector.Protect"/>: nonce(12) | ciphertext | tag(16).</summary>
#pragma warning disable CA1819 // EF entity blob column
    public byte[] EncryptedPassword { get; set; } = [];
#pragma warning restore CA1819

    public override string ToString() => $"DeviceCredential {DeviceId} ({UserName})";
}
