using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Oadm.Core.Tests.Discovery;

/// <summary>Builds synthetic mDNS responses (all records in the answer section, no compression).</summary>
internal sealed class DnsResponseBuilder
{
    private readonly List<byte[]> _records = [];

    public DnsResponseBuilder Ptr(string name, string target, uint ttl = 120)
        => Add(name, 12, ttl, false, EncodeName(target));

    public DnsResponseBuilder Srv(string name, string target, ushort port, uint ttl = 120)
    {
        var rdata = new byte[6].Concat(EncodeName(target)).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(rdata.AsSpan(4), port);
        return Add(name, 33, ttl, true, rdata);
    }

    public DnsResponseBuilder Txt(string name, params string[] entries)
    {
        var rdata = entries.SelectMany(e =>
        {
            var b = Encoding.UTF8.GetBytes(e);
            return new[] { (byte)b.Length }.Concat(b);
        }).ToArray();
        return Add(name, 16, 4500, true, rdata);
    }

    public DnsResponseBuilder A(string name, string address, bool cacheFlush = true)
        => Add(name, 1, 120, cacheFlush, IPAddress.Parse(address).GetAddressBytes());

    public byte[] Build()
    {
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 0x8400);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)_records.Count);
        return header.Concat(_records.SelectMany(r => r)).ToArray();
    }

    private DnsResponseBuilder Add(string name, ushort type, uint ttl, bool flush, byte[] rdata)
    {
        var fixedPart = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart, type);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(2), (ushort)(flush ? 0x8001 : 0x0001));
        BinaryPrimitives.WriteUInt32BigEndian(fixedPart.AsSpan(4), ttl);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(8), (ushort)rdata.Length);
        _records.Add(EncodeName(name).Concat(fixedPart).Concat(rdata).ToArray());
        return this;
    }

    private static byte[] EncodeName(string name)
    {
        var result = new List<byte>();
        foreach (var label in name.Split('.'))
        {
            var b = Encoding.UTF8.GetBytes(label);
            result.Add((byte)b.Length);
            result.AddRange(b);
        }

        result.Add(0);
        return result.ToArray();
    }
}
