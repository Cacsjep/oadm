using System.Security.Cryptography;

namespace Oadm.Sites.Binding;

/// <summary>
/// The 8-character pairing code of a site node: Crockford base32 (no I, L, O, U), 40 bits. Shown as "ABCD-EFGH"; typed
/// in any case, with or without the dash; I and L read as 1, O as 0.
/// </summary>
public static class PairingCode
{
    public const int Length = 8;

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>A new random code (normalized, without the dash).</summary>
    public static string Create()
    {
        Span<char> code = stackalloc char[Length];
        for (var i = 0; i < Length; i++)
            code[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(code);
    }

    /// <summary>The code as the app shows it: "ABCD-EFGH".</summary>
    public static string Format(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return code.Length == Length ? $"{code[..4]}-{code[4..]}" : code;
    }

    /// <summary>The typed code in its stored form, or null when it is not 8 characters of the alphabet.</summary>
    public static string? Normalize(string? typed)
    {
        if (typed is null)
            return null;
        Span<char> code = stackalloc char[Length];
        var n = 0;
        foreach (var raw in typed)
        {
            if (raw is '-' or ' ')
                continue;
            var c = char.ToUpperInvariant(raw) switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                var other => other,
            };
            if (Alphabet.IndexOf(c, StringComparison.Ordinal) < 0 || n == Length)
                return null;
            code[n++] = c;
        }
        return n == Length ? new string(code) : null;
    }
}
