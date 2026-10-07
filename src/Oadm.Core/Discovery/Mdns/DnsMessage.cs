using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Oadm.Core.Discovery.Mdns;

/// <summary>DNS resource record types used by the mDNS browser.</summary>
public enum DnsRecordType : ushort
{
    A = 1,
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "DNS record type mnemonic.")]
    Ptr = 12,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,
    Any = 255,
}

/// <summary>Base type of a parsed DNS resource record.</summary>
/// <param name="Name">Owner name, without trailing dot.</param>
/// <param name="Type">Record type (raw value, may be a type this parser does not decode).</param>
/// <param name="Class">Record class without the mDNS cache-flush bit.</param>
/// <param name="CacheFlush">mDNS cache-flush bit (top bit of the class field).</param>
/// <param name="Ttl">Time to live in seconds.</param>
public abstract record DnsRecord(string Name, DnsRecordType Type, ushort Class, bool CacheFlush, uint Ttl);

public sealed record PtrRecord(string Name, ushort Class, bool CacheFlush, uint Ttl, string DomainName)
    : DnsRecord(Name, DnsRecordType.Ptr, Class, CacheFlush, Ttl);

public sealed record SrvRecord(string Name, ushort Class, bool CacheFlush, uint Ttl, ushort Priority, ushort Weight, ushort Port, string Target)
    : DnsRecord(Name, DnsRecordType.Srv, Class, CacheFlush, Ttl);

public sealed record TxtRecord(string Name, ushort Class, bool CacheFlush, uint Ttl, IReadOnlyList<string> Entries)
    : DnsRecord(Name, DnsRecordType.Txt, Class, CacheFlush, Ttl)
{
    /// <summary>
    /// TXT entries as key/value pairs (RFC 6763 section 6). Keys are case-insensitive, the first
    /// occurrence wins, entries without '=' map to an empty value.
    /// </summary>
    public IReadOnlyDictionary<string, string> ToDictionary()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            if (entry.Length == 0)
            {
                continue;
            }

            var eq = entry.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? entry : entry[..eq];
            var value = eq < 0 ? string.Empty : entry[(eq + 1)..];
            if (key.Length > 0)
            {
                result.TryAdd(key, value);
            }
        }

        return result;
    }
}

public sealed record AddressRecord(string Name, DnsRecordType Type, ushort Class, bool CacheFlush, uint Ttl, IPAddress Address)
    : DnsRecord(Name, Type, Class, CacheFlush, Ttl);

/// <summary>A record of a type this parser does not decode.</summary>
public sealed record UnknownRecord(string Name, DnsRecordType Type, ushort Class, bool CacheFlush, uint Ttl)
    : DnsRecord(Name, Type, Class, CacheFlush, Ttl);

public sealed record DnsQuestion(string Name, DnsRecordType Type, bool UnicastResponse);

/// <summary>Parsed DNS message (header, questions and all record sections).</summary>
public sealed record DnsMessage(
    ushort Id,
    bool IsResponse,
    IReadOnlyList<DnsQuestion> Questions,
    IReadOnlyList<DnsRecord> Answers,
    IReadOnlyList<DnsRecord> Authorities,
    IReadOnlyList<DnsRecord> Additionals)
{
    /// <summary>Answers, authorities and additionals in one sequence.</summary>
    public IEnumerable<DnsRecord> AllRecords => Answers.Concat(Authorities).Concat(Additionals);

    /// <summary>Parses a DNS message. Returns false for malformed or truncated input.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out DnsMessage? message)
    {
        message = null;
        try
        {
            message = Parse(data);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Parses a DNS message. Throws <see cref="FormatException"/> on malformed input.</summary>
    public static DnsMessage Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
        {
            throw new FormatException("DNS message shorter than header.");
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(data);
        var flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        var qd = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        var an = BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        var ns = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        var ar = BinaryPrimitives.ReadUInt16BigEndian(data[10..]);

        var offset = 12;
        var questions = new List<DnsQuestion>(qd);
        for (var i = 0; i < qd; i++)
        {
            var name = ReadName(data, ref offset);
            EnsureAvailable(data, offset, 4);
            var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
            var cls = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
            offset += 4;
            questions.Add(new DnsQuestion(name, type, (cls & 0x8000) != 0));
        }

        var answers = ReadRecords(data, ref offset, an);
        var authorities = ReadRecords(data, ref offset, ns);
        var additionals = ReadRecords(data, ref offset, ar);
        return new DnsMessage(id, (flags & 0x8000) != 0, questions, answers, authorities, additionals);
    }

    /// <summary>Builds a standard query message (id, flags 0) for the given questions.</summary>
    public static byte[] BuildQuery(ushort id, IEnumerable<DnsQuestion> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        var list = questions.ToList();
        using var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], (ushort)list.Count);
        ms.Write(header);
        Span<byte> tail = stackalloc byte[4];
        foreach (var q in list)
        {
            WriteName(ms, q.Name);
            BinaryPrimitives.WriteUInt16BigEndian(tail, (ushort)q.Type);
            BinaryPrimitives.WriteUInt16BigEndian(tail[2..], (ushort)(1 | (q.UnicastResponse ? 0x8000 : 0)));
            ms.Write(tail);
        }

        return ms.ToArray();
    }

    private static void WriteName(MemoryStream ms, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new ArgumentException($"Invalid DNS label in '{name}'.", nameof(name));
            }

            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }

        ms.WriteByte(0);
    }

    private static List<DnsRecord> ReadRecords(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        var records = new List<DnsRecord>(count);
        for (var i = 0; i < count; i++)
        {
            records.Add(ReadRecord(data, ref offset));
        }

        return records;
    }

    private static DnsRecord ReadRecord(ReadOnlySpan<byte> data, ref int offset)
    {
        var name = ReadName(data, ref offset);
        EnsureAvailable(data, offset, 10);
        var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        var rawClass = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 2)..]);
        var ttl = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
        var rdLength = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 8)..]);
        offset += 10;
        EnsureAvailable(data, offset, rdLength);
        var rdStart = offset;
        var rdEnd = offset + rdLength;
        offset = rdEnd;

        var cls = (ushort)(rawClass & 0x7FFF);
        var flush = (rawClass & 0x8000) != 0;
        switch (type)
        {
            case DnsRecordType.A when rdLength == 4:
                return new AddressRecord(name, type, cls, flush, ttl, new IPAddress(data.Slice(rdStart, 4)));
            case DnsRecordType.Aaaa when rdLength == 16:
                return new AddressRecord(name, type, cls, flush, ttl, new IPAddress(data.Slice(rdStart, 16)));
            case DnsRecordType.Ptr:
            {
                var p = rdStart;
                var target = ReadName(data, ref p);
                return new PtrRecord(name, cls, flush, ttl, target);
            }

            case DnsRecordType.Srv when rdLength >= 7:
            {
                var priority = BinaryPrimitives.ReadUInt16BigEndian(data[rdStart..]);
                var weight = BinaryPrimitives.ReadUInt16BigEndian(data[(rdStart + 2)..]);
                var port = BinaryPrimitives.ReadUInt16BigEndian(data[(rdStart + 4)..]);
                var p = rdStart + 6;
                var target = ReadName(data, ref p);
                return new SrvRecord(name, cls, flush, ttl, priority, weight, port, target);
            }

            case DnsRecordType.Txt:
            {
                var entries = new List<string>();
                var p = rdStart;
                while (p < rdEnd)
                {
                    var len = data[p++];
                    if (p + len > rdEnd)
                    {
                        throw new FormatException("TXT string exceeds record data.");
                    }

                    entries.Add(Encoding.UTF8.GetString(data.Slice(p, len)));
                    p += len;
                }

                return new TxtRecord(name, cls, flush, ttl, entries);
            }

            default:
                return new UnknownRecord(name, type, cls, flush, ttl);
        }
    }

    private static string ReadName(ReadOnlySpan<byte> data, ref int offset)
    {
        var sb = new StringBuilder();
        var position = offset;
        var jumped = false;
        var jumps = 0;
        while (true)
        {
            EnsureAvailable(data, position, 1);
            var len = data[position];
            if (len == 0)
            {
                position++;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                EnsureAvailable(data, position, 2);
                var pointer = ((len & 0x3F) << 8) | data[position + 1];
                if (!jumped)
                {
                    offset = position + 2;
                }

                jumped = true;
                if (++jumps > 32 || pointer >= data.Length)
                {
                    throw new FormatException("Invalid DNS name compression pointer.");
                }

                position = pointer;
                continue;
            }

            if ((len & 0xC0) != 0)
            {
                throw new FormatException("Unsupported DNS label type.");
            }

            position++;
            EnsureAvailable(data, position, len);
            if (sb.Length > 0)
            {
                sb.Append('.');
            }

            sb.Append(Encoding.UTF8.GetString(data.Slice(position, len)));
            position += len;
            if (sb.Length > 1024)
            {
                throw new FormatException("DNS name too long.");
            }
        }

        if (!jumped)
        {
            offset = position;
        }

        return sb.ToString();
    }

    private static void EnsureAvailable(ReadOnlySpan<byte> data, int offset, int count)
    {
        if (offset < 0 || count < 0 || offset + count > data.Length)
        {
            throw new FormatException("DNS message truncated.");
        }
    }
}
