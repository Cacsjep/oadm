using System.Net;

using Oadm.Sites.Transport;

namespace Oadm.Server.Auth;

/// <summary>
/// Failed logins per client address (docs/spec/security.md, Tokens): after <see cref="MaxFailures"/> failed logins within
/// <see cref="Window"/> every login from that address is refused until the window ends, whatever the user name. The
/// number is generous on purpose: several users share one address behind a NAT, and a single user name locks after 5
/// failures already (<see cref="Core.Auth.LoginThrottle"/>). IPv6 counts per /64. Server memory only.
/// </summary>
public sealed class LoginAddressLimit(TimeProvider time)
{
    public const int MaxFailures = 50;
    public const string Message = "Too many failed logins from your address. Try again in a few minutes.";
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly AddressRateLimiter _failures = new(MaxFailures, total: 0, Window, time);

    /// <summary>True while logins from <paramref name="clientAddress"/> are refused.</summary>
    public bool IsLimited(string clientAddress) => _failures.IsLimited(KeyOf(clientAddress));

    /// <summary>Counts a failed login; true when it was the address's last one before the limit.</summary>
    public bool RecordFailure(string clientAddress)
    {
        var key = KeyOf(clientAddress);
        return _failures.TryAcquire(key) && _failures.IsLimited(key);
    }

    private static string KeyOf(string clientAddress) =>
        IPAddress.TryParse(clientAddress, out var address) ? AddressRateLimiter.KeyOf(address) : clientAddress;
}
