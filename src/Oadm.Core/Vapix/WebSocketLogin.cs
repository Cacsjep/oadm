using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Oadm.Core.Vapix;

/// <summary>
/// The Authorization header for a request of the web interface relay: WebSocket upgrades the device refused with 401,
/// and every relayed request, which logs in on its first try like a browser. The HTTP handler answers login
/// challenges for normal requests but not for WebSocket upgrades, so the relay of a device's web interface answers it
/// here: Digest (MD5 or SHA-256, qop auth) on any scheme, Basic only over HTTPS. Never logged.
/// </summary>
public static class WebSocketLogin
{
    /// <summary>The header value for the strongest challenge, or null when none can be answered.</summary>
    /// <param name="challenges">The WWW-Authenticate values of the 401.</param>
    /// <param name="pathAndQuery">The request target, "/local/app/ws?x=1".</param>
    public static string? Answer(IEnumerable<string> challenges, string pathAndQuery, NetworkCredential credential, bool https, string? cnonce = null,
        string method = "GET", int nonceCount = 1)
    {
        ArgumentNullException.ThrowIfNull(challenges);
        ArgumentNullException.ThrowIfNull(credential);
        var list = challenges.ToList();
        foreach (var challenge in list)
        {
            if (challenge.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase) && Digest(challenge[7..], pathAndQuery, credential, cnonce, method, nonceCount) is { } digest)
            {
                return digest;
            }
        }

        if (https && list.Any(c => c.StartsWith("Basic", StringComparison.OrdinalIgnoreCase)))
        {
            return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(credential.UserName + ":" + credential.Password));
        }

        return null;
    }

    private static string? Digest(string challenge, string uri, NetworkCredential credential, string? cnonce, string method, int nonceCount)
    {
        var fields = Parse(challenge);
        if (!fields.TryGetValue("realm", out var realm) || !fields.TryGetValue("nonce", out var nonce))
        {
            return null;
        }

        var algorithm = fields.GetValueOrDefault("algorithm", "MD5");
        Func<string, string> hash = algorithm.ToUpperInvariant() switch
        {
            "MD5" => Md5,
            "SHA-256" => Sha256,
            _ => null!,
        };
        if (hash is null)
        {
            return null;
        }

        var qops = fields.GetValueOrDefault("qop", string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var qop = qops.Contains("auth", StringComparer.OrdinalIgnoreCase) ? "auth" : null;
        var nc = nonceCount.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
        cnonce ??= Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var ha1 = hash($"{credential.UserName}:{realm}:{credential.Password}");
        var ha2 = hash($"{method}:{uri}");
        var response = qop is null ? hash($"{ha1}:{nonce}:{ha2}") : hash($"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");
        var header = new StringBuilder()
            .Append("Digest username=\"").Append(credential.UserName).Append("\", realm=\"").Append(realm)
            .Append("\", nonce=\"").Append(nonce).Append("\", uri=\"").Append(uri).Append("\", algorithm=").Append(algorithm)
            .Append(", response=\"").Append(response).Append('"');
        if (qop is not null)
        {
            header.Append(", qop=").Append(qop).Append(", nc=").Append(nc).Append(", cnonce=\"").Append(cnonce).Append('"');
        }

        if (fields.TryGetValue("opaque", out var opaque))
        {
            header.Append(", opaque=\"").Append(opaque).Append('"');
        }

        return header.ToString();
    }

    /// <summary>The name="value" and name=value pairs of a challenge.</summary>
    internal static Dictionary<string, string> Parse(string challenge)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < challenge.Length)
        {
            while (i < challenge.Length && (challenge[i] == ' ' || challenge[i] == ','))
            {
                i++;
            }

            var eq = challenge.IndexOf('=', i);
            if (eq < 0)
            {
                break;
            }

            var name = challenge[i..eq].Trim();
            i = eq + 1;
            string value;
            if (i < challenge.Length && challenge[i] == '"')
            {
                var end = challenge.IndexOf('"', i + 1);
                end = end < 0 ? challenge.Length : end;
                value = challenge[(i + 1)..end];
                i = end + 1;
            }
            else
            {
                var end = challenge.IndexOf(',', i);
                end = end < 0 ? challenge.Length : end;
                value = challenge[i..end].Trim();
                i = end;
            }

            fields[name] = value;
        }

        return fields;
    }

#pragma warning disable CA5351 // Digest MD5 is what the device asks for (RFC 7616); it is no security measure of OADM
    private static string Md5(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));
#pragma warning restore CA5351

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>For tests: the hex digest of a text.</summary>
    internal static string Hash(string algorithm, string text) =>
        algorithm.Equals("SHA-256", StringComparison.OrdinalIgnoreCase) ? Sha256(text) : Md5(text);
}
