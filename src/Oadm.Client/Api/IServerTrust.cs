using Oadm.Contracts.Security;

namespace Oadm.Client.Api;

/// <summary>
/// The TLS trust of the current server (trust on first use of its self-signed certificate), used by the login window:
/// after a failed connection it tells whether the certificate is new (confirm the fingerprint) or changed (refused,
/// Forget server re-pins).
/// </summary>
public interface IServerTrust
{
    /// <summary>Outcome of the last TLS handshake with the current server.</summary>
    PinCheck LastCheck { get; }

    /// <summary>Fingerprint the current server presented in the last handshake.</summary>
    string? PresentedFingerprint { get; }

    /// <summary>Clears <see cref="LastCheck"/> before a new attempt.</summary>
    void ResetCheck();

    /// <summary>Pins <paramref name="fingerprint"/> for the current server and reconnects.</summary>
    void Trust(string fingerprint);

    /// <summary>Removes the pin (and a remembered login) of the current server.</summary>
    void Forget();
}

/// <summary>Trust of a <see cref="GrpcOadmApi"/>.</summary>
public sealed class GrpcServerTrust(GrpcOadmApi api) : IServerTrust
{
    public PinCheck LastCheck => api.ServerUri.Scheme == Uri.UriSchemeHttps ? api.Pinning.LastCheck(api.ServerUri) : PinCheck.None;

    public string? PresentedFingerprint => api.Pinning.PresentedFingerprint(api.ServerUri);

    public void ResetCheck() => api.Pinning.ResetCheck(api.ServerUri);

    public void Trust(string fingerprint)
    {
        api.Pinning.Trust(api.ServerUri, fingerprint);
        api.Reconnect();
    }

    public void Forget()
    {
        api.Pinning.Forget(api.ServerUri);
        api.Reconnect();
    }
}

/// <summary>Fake mode: no TLS.</summary>
public sealed class NoServerTrust : IServerTrust
{
    public PinCheck LastCheck => PinCheck.None;

    public string? PresentedFingerprint => null;

    public void ResetCheck()
    {
    }

    public void Trust(string fingerprint)
    {
    }

    public void Forget()
    {
    }
}
