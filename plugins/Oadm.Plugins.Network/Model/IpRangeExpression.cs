using System.Globalization;

namespace Oadm.Plugins.Network.Model;

/// <summary>
/// An IPv4 address range as AXIS Device Manager / AXIS Camera Station accept it in "Assign IP address":
/// <list type="bullet">
/// <item>wildcards per octet: <c>192.168.0.*</c>, <c>10.*.1.*</c></item>
/// <item>first and last address: <c>192.168.0.10-192.168.0.20</c>, shortened <c>192.168.0.10-20</c></item>
/// <item>a range in any octet, also combined with wildcards: <c>10.10-30.1.101</c>, <c>10.10-30.1.*</c></item>
/// <item>several ranges separated by commas: <c>192.168.0.*,192.168.1.10-192.168.1.20</c></item>
/// <item>OADM addition: a single address alone is a start address, the devices get consecutive addresses from it
/// up to the end of the subnet. Inside a list a single address is just that address.</item>
/// </list>
/// Addresses are enumerated lazily in the written order (parts left to right, first octet outermost).
/// </summary>
public sealed class IpRangeExpression
{
    /// <summary>Upper bound of addresses one expression may describe (a /8 with wildcards is refused).</summary>
    public const long MaxAddresses = 1 << 20;

    private readonly IReadOnlyList<Part> _parts;

    private IpRangeExpression(string text, IReadOnlyList<Part> parts, bool isStartAddress)
    {
        Text = text;
        _parts = parts;
        IsStartAddress = isStartAddress;
    }

    public string Text { get; }

    /// <summary>The expression is one plain address: consecutive addresses from there.</summary>
    public bool IsStartAddress { get; }

    /// <summary>Number of addresses described (for a start address: 1, the rest depends on the subnet).</summary>
    public long Count => _parts.Sum(p => p.Count);

    /// <summary>Parses the expression; on failure <paramref name="error"/> says what is wrong (for the dialog).</summary>
    public static bool TryParse(string? text, out IpRangeExpression? expression, out string? error)
    {
        expression = null;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Enter an IP range, e.g. 192.168.0.* or 192.168.0.10-20.";
            return false;
        }

        var parts = new List<Part>();
        foreach (var raw in text.Split(',', StringSplitOptions.TrimEntries))
        {
            if (raw.Length == 0)
            {
                error = "The IP range has an empty entry between commas.";
                return false;
            }

            if (!TryParsePart(raw.Replace(" ", string.Empty, StringComparison.Ordinal), out var part, out error))
            {
                return false;
            }

            parts.Add(part!);
        }

        var total = parts.Sum(p => p.Count);
        if (total > MaxAddresses)
        {
            error = $"The IP range describes {total:N0} addresses; use at most {MaxAddresses:N0}.";
            return false;
        }

        var isStart = parts.Count == 1 && parts[0].Octets.Length == 4 && parts[0].Count == 1;
        expression = new IpRangeExpression(text.Trim(), parts, isStart);
        return true;
    }

    /// <summary>
    /// The addresses in order. A start address continues with the next addresses until the broadcast address of its
    /// /<paramref name="prefixLength"/> subnet (or the end of the address space).
    /// </summary>
    public IEnumerable<uint> Enumerate(int prefixLength)
    {
        if (IsStartAddress)
        {
            var first = _parts[0].Octets.Aggregate(0u, (acc, o) => (acc << 8) | (uint)o.From);
            var last = prefixLength is > 0 and <= 30 ? Ipv4.Broadcast(first, prefixLength) : uint.MaxValue;
            for (ulong a = first; a <= last; a++)
            {
                yield return (uint)a;
            }

            yield break;
        }

        foreach (var part in _parts)
        {
            foreach (var address in part.Enumerate())
            {
                yield return address;
            }
        }
    }

    private static bool TryParsePart(string text, out Part? part, out string? error)
    {
        part = null;
        error = null;

        // Full form "a.b.c.d-e.f.g.h" (or shortened, the right side replacing the last octets: "a.b.c.d-x").
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var left = dash < 0 ? text : text[..dash];
        if (dash >= 0 && left.Count(c => c == '.') == 3 && text[(dash + 1)..].Contains('.', StringComparison.Ordinal))
        {
            return TryParseFromTo(text, left, text[(dash + 1)..], out part, out error);
        }

        var tokens = text.Split('.');
        if (tokens.Length != 4)
        {
            error = $"\"{text}\" is not an IP range: write four parts separated by dots, e.g. 192.168.0.*.";
            return false;
        }

        var octets = new OctetRange[4];
        for (var i = 0; i < 4; i++)
        {
            if (!TryParseOctet(tokens[i], out octets[i]))
            {
                error = $"\"{text}\" is not an IP range: \"{tokens[i]}\" must be a number 0-255, a range like 10-20 or *.";
                return false;
            }
        }

        part = new Part(octets);
        return true;
    }

    private static bool TryParseFromTo(string text, string from, string to, out Part? part, out string? error)
    {
        part = null;
        error = null;
        if (!Ipv4.TryParse(from, out var first))
        {
            error = $"\"{from}\" in \"{text}\" is not a valid IPv4 address.";
            return false;
        }

        var right = to.Split('.');
        if (right.Length > 4)
        {
            error = $"\"{to}\" in \"{text}\" is not a valid IPv4 address.";
            return false;
        }

        // A shortened right side replaces the last octets of the left side ("10.0.0.10-1.20" = 10.0.0.10-10.0.1.20).
        var leftOctets = from.Split('.');
        var full = string.Join('.', leftOctets.Take(4 - right.Length).Concat(right));
        if (!Ipv4.TryParse(full, out var last))
        {
            error = $"\"{to}\" in \"{text}\" is not a valid IPv4 address.";
            return false;
        }

        if (last < first)
        {
            error = $"\"{text}\": the last address is lower than the first.";
            return false;
        }

        part = new Part(first, last);
        return true;
    }

    private static bool TryParseOctet(string token, out OctetRange range)
    {
        range = default;
        if (token == "*")
        {
            range = new OctetRange(0, 255);
            return true;
        }

        var dash = token.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            if (!TryOctet(token, out var single))
            {
                return false;
            }

            range = new OctetRange(single, single);
            return true;
        }

        if (!TryOctet(token[..dash], out var from) || !TryOctet(token[(dash + 1)..], out var to) || to < from)
        {
            return false;
        }

        range = new OctetRange(from, to);
        return true;
    }

    private static bool TryOctet(string token, out int value)
    {
        value = 0;
        return token.Length is > 0 and <= 3 && token.All(char.IsAsciiDigit) && (token.Length == 1 || token[0] != '0')
            && int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= 255;
    }

    private readonly record struct OctetRange(int From, int To)
    {
        public int Count => To - From + 1;
    }

    /// <summary>Either four octet ranges (wildcards, per-octet ranges) or a from-to span.</summary>
    private sealed class Part
    {
        private readonly uint _from;
        private readonly uint _to;

        public Part(OctetRange[] octets)
        {
            Octets = octets;
            Count = octets.Aggregate(1L, (acc, o) => acc * o.Count);
        }

        public Part(uint from, uint to)
        {
            _from = from;
            _to = to;
            Octets = [];
            Count = (long)to - from + 1;
        }

        public OctetRange[] Octets { get; }

        public long Count { get; }

        public IEnumerable<uint> Enumerate()
        {
            if (Octets.Length == 0)
            {
                for (ulong a = _from; a <= _to; a++)
                {
                    yield return (uint)a;
                }

                yield break;
            }

            for (var a = Octets[0].From; a <= Octets[0].To; a++)
            {
                for (var b = Octets[1].From; b <= Octets[1].To; b++)
                {
                    for (var c = Octets[2].From; c <= Octets[2].To; c++)
                    {
                        for (var d = Octets[3].From; d <= Octets[3].To; d++)
                        {
                            yield return ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | (uint)d;
                        }
                    }
                }
            }
        }
    }
}
