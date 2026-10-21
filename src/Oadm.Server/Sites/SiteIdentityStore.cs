using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Core.Sites;
using Oadm.Sites.Transport;

namespace Oadm.Server.Sites;

/// <summary>
/// The server's site identity (docs/spec/sites.md, Server identity): an ECDSA P-256 key and self-signed certificate the server
/// presents to site nodes, apart from its TLS certificate, so a new TLS certificate or a moved server keeps its sites.
/// Kept in the setting <see cref="SiteSettings.Identity"/> as PKCS#12, encrypted with the master key (purpose
/// <see cref="SiteSettings.IdentityPurpose"/>), so it travels with a backup (oadm.db + master.key). A key that cannot be
/// read (master key replaced) is made new; the nodes then ask to accept the new identity in their app.
/// </summary>
/// <remarks>
/// On Windows a loaded PKCS#12 key lives in a file of the key store until its certificate is disposed (an ephemeral key
/// set cannot be used: SChannel refuses it for the server side of a handshake, checked on Windows 10 1809). .NET runs no
/// finalizers at exit, so <see cref="Dispose"/> disposes every identity this store loaded or made (the current one and
/// those replaced by <see cref="RotateAsync"/>): a server stop leaves no key file behind.
/// </remarks>
public sealed partial class SiteIdentityStore(ServerSettingsStore settings, CredentialProtector protector, ILogger<SiteIdentityStore> logger) : IDisposable
{
    private readonly PluginSecretProtector _protector = new(protector);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private X509Certificate2? _current;
    private readonly List<X509Certificate2> _retired = [];

    /// <summary>The identity (after <see cref="LoadOrCreateAsync"/>).</summary>
    public X509Certificate2 Current => Volatile.Read(ref _current) ?? throw new InvalidOperationException("The site identity is not loaded.");

    /// <summary>SHA-256 of the identity certificate (what the nodes pin).</summary>
    public byte[] Fingerprint => SiteTls.Fingerprint(Current);

    public string FingerprintHex => SiteTls.FingerprintHex(Current);

    /// <summary>Loads the stored identity, or makes and stores a new one.</summary>
    public async Task<X509Certificate2> LoadOrCreateAsync(string serverName, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_current is not null)
            {
                return _current;
            }

            var json = await settings.GetJsonAsync(SiteSettings.Identity, ct).ConfigureAwait(false);
            if (json is not null && Read(json) is { } stored)
            {
                Volatile.Write(ref _current, stored);
                return stored;
            }

            if (json is not null)
            {
                LogUnreadable();
            }

            var created = SiteTls.CreateIdentity(Name(serverName));
            await StoreAsync(created, ct).ConfigureAwait(false);
            Volatile.Write(ref _current, created);
            var fingerprint = SiteTls.FingerprintHex(created);
            LogCreated(fingerprint);
            return created;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// A new identity, its certificate signed with the old key (what <c>RotateIdentity</c> sends each node). The caller
    /// sends it to the nodes; the new identity is stored and used from now on.
    /// </summary>
    public async Task<(X509Certificate2 Identity, byte[] Certificate, byte[] Signature)> RotateAsync(string serverName, CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var old = Current;
            var created = SiteTls.CreateIdentity(Name(serverName));
            _retired.Add(old); // links may still use it; disposed with the store
            var der = created.RawData;
            using var oldKey = old.GetECDsaPrivateKey() ?? throw new InvalidOperationException("The site identity has no key.");
            var signature = oldKey.SignData(der, HashAlgorithmName.SHA256);
            await StoreAsync(created, ct).ConfigureAwait(false);
            Volatile.Write(ref _current, created);
            var fingerprint = SiteTls.FingerprintHex(created);
            LogRotated(fingerprint);
            return (created, der, signature);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task StoreAsync(X509Certificate2 identity, CancellationToken ct)
    {
        var pfx = Convert.ToBase64String(identity.Export(X509ContentType.Pkcs12));
        var stored = new StoredIdentity(_protector.Protect(pfx, SiteSettings.IdentityPurpose));
        await settings.SetJsonAsync(SiteSettings.Identity, JsonSerializer.Serialize(stored), ct).ConfigureAwait(false);
    }

    private X509Certificate2? Read(string json)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<StoredIdentity>(json);
            if (stored?.Key is not { Length: > 0 } key)
            {
                return null;
            }

            var pfx = Convert.FromBase64String(_protector.Unprotect(key, SiteSettings.IdentityPurpose));
            return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var identity in _retired)
        {
            identity.Dispose();
        }

        _retired.Clear();
        Interlocked.Exchange(ref _current, null)?.Dispose();
        _lock.Dispose();
    }

    private static string Name(string serverName) => "OADM " + (string.IsNullOrWhiteSpace(serverName) ? "server" : serverName.Trim());

    private sealed record StoredIdentity(string Key);

    [LoggerMessage(Level = LogLevel.Information, Message = "Site identity created ({Fingerprint})")]
    private partial void LogCreated(string fingerprint);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Site identity rotated ({Fingerprint}); site nodes that were not connected ask to accept it")]
    private partial void LogRotated(string fingerprint);

    [LoggerMessage(Level = LogLevel.Error, Message = "The site identity cannot be read (another master key): a new one is made. Each site node asks to accept it in the OADM Site Node app.")]
    private partial void LogUnreadable();
}
