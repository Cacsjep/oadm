using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Oadm.Core.LiveView.Rtsp;

/// <summary>
/// HTTP Digest authentication (RFC 2617, MD5 and MD5-sess, qop=auth or none) for RTSP requests.
/// Basic is deliberately not supported: RTSP runs over plain TCP and Basic would send the
/// password in clear text.
/// </summary>
public sealed class DigestAuthenticator
{
    private readonly NetworkCredential _credentials;
    private readonly Dictionary<string, string> _challenge;
    private int _nonceCount;

    private readonly Func<string> _clientNonce;

    private DigestAuthenticator(NetworkCredential credentials, Dictionary<string, string> challenge, Func<string>? clientNonce)
    {
        _credentials = credentials;
        _challenge = challenge;
        _clientNonce = clientNonce ?? (() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)));
    }

    public string Realm => _challenge.GetValueOrDefault("realm") ?? string.Empty;

    /// <summary>Picks the Digest challenge from WWW-Authenticate values, or null when none is offered.</summary>
    /// <param name="clientNonce">Client nonce generator; tests pass a fixed one.</param>
    public static DigestAuthenticator? FromChallenges(IEnumerable<string> wwwAuthenticate, NetworkCredential credentials, Func<string>? clientNonce = null)
    {
        ArgumentNullException.ThrowIfNull(wwwAuthenticate);
        ArgumentNullException.ThrowIfNull(credentials);
        foreach (var value in wwwAuthenticate)
        {
            if (value.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase))
            {
                var parameters = ParseParameters(value["Digest ".Length..]);
                if (parameters.ContainsKey("nonce"))
                {
                    return new DigestAuthenticator(credentials, parameters, clientNonce);
                }
            }
        }

        return null;
    }

    /// <summary>The Authorization header value for one request.</summary>
    public string Authorize(string method, string uri)
    {
        var realm = Realm;
        var nonce = _challenge["nonce"];
        var algorithm = _challenge.GetValueOrDefault("algorithm") ?? "MD5";
        var qop = _challenge.GetValueOrDefault("qop")?.Split(',').Select(q => q.Trim()).FirstOrDefault(q => q.Equals("auth", StringComparison.OrdinalIgnoreCase));
        var cnonce = _clientNonce();

        var ha1 = Md5Hex($"{_credentials.UserName}:{realm}:{_credentials.Password}");
        if (algorithm.Equals("MD5-sess", StringComparison.OrdinalIgnoreCase))
        {
            ha1 = Md5Hex($"{ha1}:{nonce}:{cnonce}");
        }

        var ha2 = Md5Hex($"{method}:{uri}");
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Digest username=\"{_credentials.UserName}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\"");
        string response;
        if (qop is not null)
        {
            var nc = (++_nonceCount).ToString("x8", CultureInfo.InvariantCulture);
            response = Md5Hex($"{ha1}:{nonce}:{nc}:{cnonce}:{qop}:{ha2}");
            sb.Append(CultureInfo.InvariantCulture, $", qop={qop}, nc={nc}, cnonce=\"{cnonce}\"");
        }
        else
        {
            response = Md5Hex($"{ha1}:{nonce}:{ha2}");
        }

        sb.Append(CultureInfo.InvariantCulture, $", response=\"{response}\"");
        if (_challenge.TryGetValue("opaque", out var opaque))
        {
            sb.Append(CultureInfo.InvariantCulture, $", opaque=\"{opaque}\"");
        }

        if (_challenge.ContainsKey("algorithm"))
        {
            sb.Append(CultureInfo.InvariantCulture, $", algorithm={algorithm}");
        }

        return sb.ToString();
    }

    internal static Dictionary<string, string> ParseParameters(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (text[i] == ',' || char.IsWhiteSpace(text[i])))
            {
                i++;
            }

            var eq = text.IndexOf('=', i);
            if (eq < 0)
            {
                break;
            }

            var name = text[i..eq].Trim();
            i = eq + 1;
            string value;
            if (i < text.Length && text[i] == '"')
            {
                var end = text.IndexOf('"', i + 1);
                if (end < 0)
                {
                    end = text.Length;
                }

                value = text[(i + 1)..end];
                i = end + 1;
            }
            else
            {
                var end = text.IndexOf(',', i);
                if (end < 0)
                {
                    end = text.Length;
                }

                value = text[i..end].Trim();
                i = end;
            }

            result[name] = value;
        }

        return result;
    }

    // MD5 is mandated by the Digest scheme the cameras offer; it is not used for anything else.
#pragma warning disable CA5351
    private static string Md5Hex(string text) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(text)));
#pragma warning restore CA5351
}
