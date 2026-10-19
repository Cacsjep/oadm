using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Oadm.SiteNode.Discovery;

/// <summary>DNS record types the node reads and writes.</summary>
public enum DnsType : ushort
{
    A = 1,
    DomainPointer = 12,
    Txt = 16,
    Aaaa = 28,
    Srv = 33,
    Any = 255,
}

public sealed record DnsQuestion(string Name, DnsType Type);

/// <summary>One resource record; the fields that do not apply to its type stay empty.</summary>
public sealed record DnsRecord(string Name, DnsType Type, uint Ttl)
{
    public string Target { get; init; } = "";
    public int Port { get; init; }
    public IPAddress? Address { get; init; }
    public IReadOnlyList<string> Texts { get; init; } = [];
}

/// <summary>A parsed DNS message.</summary>
public sealed record DnsPacket(ushort Id, bool IsResponse, IReadOnlyList<DnsQuestion> Questions, IReadOnlyList<DnsRecord> Records);

/// <summary>
/// The small part of the DNS wire format mDNS needs (RFC 1035, 6762, 6763): queries, PTR, SRV, TXT, A and AAAA records,
/// name compression on reading. The node's own copy of the server's mDNS code (Oadm.Core.Discovery.Mdns), see the node
/// README: both should move into one shared library.
/// </summary>
public static class DnsWire
{
    private const int MaxNameJumps = 32;

    public static byte[] Query(ushort id, params DnsQuestion[] questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        using var stream = new MemoryStream();
        WriteHeader(stream, id, 0, questions.Length, 0);
        foreach (var question in questions)
        {
            WriteName(stream, question.Name);
            WriteUInt16(stream, (ushort)question.Type);
            WriteUInt16(stream, 1); // IN
        }

        return stream.ToArray();
    }

    /// <summary>An authoritative answer with <paramref name="questions"/> echoed (legacy unicast) and the records.</summary>
    public static byte[] Response(ushort id, IReadOnlyList<DnsQuestion> questions, IReadOnlyList<DnsRecord> answers)
    {
        ArgumentNullException.ThrowIfNull(questions);
        ArgumentNullException.ThrowIfNull(answers);
        using var stream = new MemoryStream();
        WriteHeader(stream, id, 0x8400, questions.Count, answers.Count);
        foreach (var question in questions)
        {
            WriteName(stream, question.Name);
            WriteUInt16(stream, (ushort)question.Type);
            WriteUInt16(stream, 1);
        }

        foreach (var record in answers)
        {
            WriteName(stream, record.Name);
            WriteUInt16(stream, (ushort)record.Type);
            WriteUInt16(stream, (ushort)(record.Type is DnsType.DomainPointer ? 1 : 0x8001)); // cache flush for unique records
            WriteUInt32(stream, record.Ttl);
            var data = new MemoryStream();
            switch (record.Type)
            {
                case DnsType.DomainPointer:
                    WriteName(data, record.Target);
                    break;
                case DnsType.Srv:
                    WriteUInt16(data, 0);
                    WriteUInt16(data, 0);
                    WriteUInt16(data, (ushort)record.Port);
                    WriteName(data, record.Target);
                    break;
                case DnsType.Txt:
                    foreach (var text in record.Texts.DefaultIfEmpty(""))
                    {
                        var bytes = Encoding.UTF8.GetBytes(text);
                        data.WriteByte((byte)Math.Min(bytes.Length, 255));
                        data.Write(bytes, 0, Math.Min(bytes.Length, 255));
                    }

                    break;
                case DnsType.A or DnsType.Aaaa:
                    data.Write(record.Address!.GetAddressBytes());
                    break;
            }

            WriteUInt16(stream, (ushort)data.Length);
            data.WriteTo(stream);
        }

        return stream.ToArray();
    }

    /// <summary>Parses a message; null when it is not a valid one.</summary>
    public static DnsPacket? Parse(ReadOnlySpan<byte> data)
    {
        try
        {
            if (data.Length < 12)
                return null;
            var id = BinaryPrimitives.ReadUInt16BigEndian(data);
            var flags = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
            int qd = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
            var records = BinaryPrimitives.ReadUInt16BigEndian(data[6..]) + BinaryPrimitives.ReadUInt16BigEndian(data[8..]) + BinaryPrimitives.ReadUInt16BigEndian(data[10..]);
            var offset = 12;
            var questions = new List<DnsQuestion>();
            for (var i = 0; i < qd; i++)
            {
                var name = ReadName(data, ref offset);
                var type = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
                offset += 4;
                questions.Add(new DnsQuestion(name, (DnsType)type));
            }

            var list = new List<DnsRecord>();
            for (var i = 0; i < records; i++)
            {
                var name = ReadName(data, ref offset);
                var type = (DnsType)BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
                var ttl = BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
                int length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 8)..]);
                offset += 10;
                if (offset + length > data.Length)
                    return null;
                var rdata = offset;
                var record = new DnsRecord(name, type, ttl);
                switch (type)
                {
                    case DnsType.DomainPointer:
                        record = record with { Target = ReadName(data, ref rdata) };
                        break;
                    case DnsType.Srv when length >= 7:
                        var port = BinaryPrimitives.ReadUInt16BigEndian(data[(rdata + 4)..]);
                        rdata += 6;
                        record = record with { Port = port, Target = ReadName(data, ref rdata) };
                        break;
                    case DnsType.Txt:
                        var texts = new List<string>();
                        var end = offset + length;
                        while (rdata < end)
                        {
                            int size = data[rdata++];
                            if (rdata + size > end)
                                break;
                            texts.Add(Encoding.UTF8.GetString(data.Slice(rdata, size)));
                            rdata += size;
                        }

                        record = record with { Texts = texts };
                        break;
                    case DnsType.A when length == 4:
                    case DnsType.Aaaa when length == 16:
                        record = record with { Address = new IPAddress(data.Slice(offset, length)) };
                        break;
                }

                list.Add(record);
                offset += length;
            }

            return new DnsPacket(id, (flags & 0x8000) != 0, questions, list);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or FormatException)
        {
            return null;
        }
    }

    private static string ReadName(ReadOnlySpan<byte> data, ref int offset)
    {
        var labels = new List<string>();
        var position = offset;
        var jumped = false;
        var jumps = 0;
        while (true)
        {
            int length = data[position];
            if (length == 0)
            {
                position++;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                var pointer = ((length & 0x3F) << 8) | data[position + 1];
                if (!jumped)
                    offset = position + 2;
                jumped = true;
                if (++jumps > MaxNameJumps || pointer >= data.Length)
                    throw new FormatException("A name loops.");
                position = pointer;
                continue;
            }

            labels.Add(Encoding.UTF8.GetString(data.Slice(position + 1, length)));
            position += length + 1;
            if (labels.Count > 128)
                throw new FormatException("A name is too long.");
        }

        if (!jumped)
            offset = position;
        return string.Join('.', labels);
    }

    private static void WriteHeader(Stream stream, ushort id, ushort flags, int questions, int answers)
    {
        WriteUInt16(stream, id);
        WriteUInt16(stream, flags);
        WriteUInt16(stream, (ushort)questions);
        WriteUInt16(stream, (ushort)answers);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
    }

    private static void WriteName(Stream stream, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            var length = Math.Min(bytes.Length, 63);
            stream.WriteByte((byte)length);
            stream.Write(bytes, 0, length);
        }

        stream.WriteByte(0);
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
