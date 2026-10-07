using System.Text.RegularExpressions;

namespace Oadm.Core.Discovery;

/// <summary>
/// Parses the serial from an Axis WWW-Authenticate challenge. Axis devices use the realm
/// <c>AXIS_&lt;serial&gt;</c>. Observed on AXIS OS 12: plain HTTP answers
/// <c>Digest realm="AXIS_B8A44F631339", nonce=..., algorithm=MD5, qop="auth"</c>, HTTPS answers
/// <c>Basic realm="AXIS_B8A44F631339"</c>; both schemes are accepted.
/// </summary>
public static partial class AxisRealm
{
    /// <summary>Returns the normalized serial from the first challenge with an AXIS_ realm, or null.</summary>
    public static string? TryGetSerial(IEnumerable<string>? challenges)
    {
        if (challenges is null)
        {
            return null;
        }

        foreach (var challenge in challenges)
        {
            if (TryGetSerial(challenge) is { } serial)
            {
                return serial;
            }
        }

        return null;
    }

    /// <summary>Returns the normalized serial from one WWW-Authenticate value, or null.</summary>
    public static string? TryGetSerial(string? challenge)
    {
        if (string.IsNullOrEmpty(challenge))
        {
            return null;
        }

        var match = RealmRegex().Match(challenge);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex("""realm\s*=\s*"?AXIS_([0-9A-Fa-f]{12})(?![0-9A-Za-z])""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RealmRegex();
}
