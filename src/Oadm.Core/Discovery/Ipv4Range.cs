using System.Buffers.Binary;
using System.Collections;
using System.Net;
using System.Net.Sockets;

namespace Oadm.Core.Discovery;

/// <summary>An inclusive IPv4 address range, e.g. 10.0.0.1 - 10.0.0.254.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1710", Justification = "A range is the domain term.")]
public sealed class Ipv4Range : IReadOnlyCollection<IPAddress>
{
    /// <summary>Largest range accepted (a /16), to keep accidental huge scans out.</summary>
    public const int MaxSize = 65536;

    private readonly uint _first;
    private readonly uint _last;

    private Ipv4Range(uint first, uint last)
    {
        _first = first;
        _last = last;
    }

    public IPAddress From => ToAddress(_first);

    public IPAddress To => ToAddress(_last);

    /// <summary>Number of addresses in the range (inclusive).</summary>
    public int Count => (int)(_last - _first + 1);

    /// <summary>Creates a range. Throws <see cref="ArgumentException"/> for non-IPv4, reversed or oversized ranges.</summary>
    public static Ipv4Range Create(IPAddress from, IPAddress to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var first = ToUInt32(from, nameof(from));
        var last = ToUInt32(to, nameof(to));
        if (last < first)
        {
            throw new ArgumentException($"Range end {to} is before range start {from}.", nameof(to));
        }

        if ((ulong)last - first + 1 > MaxSize)
        {
            throw new ArgumentException($"Range {from} - {to} has more than {MaxSize} addresses.", nameof(to));
        }

        return new Ipv4Range(first, last);
    }

    /// <summary>Parses two dotted-quad strings. Returns false with an error message on invalid input.</summary>
    public static bool TryParse(string? from, string? to, out Ipv4Range? range, out string? error)
    {
        range = null;
        if (!TryParseIpv4(from, out var a))
        {
            error = $"'{from}' is not a valid IPv4 address.";
            return false;
        }

        if (!TryParseIpv4(to, out var b))
        {
            error = $"'{to}' is not a valid IPv4 address.";
            return false;
        }

        try
        {
            range = Create(a!, b!);
            error = null;
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public IEnumerator<IPAddress> GetEnumerator()
    {
        for (var v = (ulong)_first; v <= _last; v++)
        {
            yield return ToAddress((uint)v);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"{From} - {To}";

    private static bool TryParseIpv4(string? text, out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        // IPAddress.TryParse accepts shorthand like "10.1" or "10"; require four dotted parts.
        var trimmed = text.Trim();
        if (trimmed.Split('.').Length != 4
            || !IPAddress.TryParse(trimmed, out var parsed)
            || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        address = parsed;
        return true;
    }

    private static uint ToUInt32(IPAddress address, string paramName)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"{address} is not an IPv4 address.", paramName);
        }

        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static IPAddress ToAddress(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
