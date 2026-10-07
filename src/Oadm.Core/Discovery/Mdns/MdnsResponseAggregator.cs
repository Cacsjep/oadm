using System.Net;
using System.Net.Sockets;

namespace Oadm.Core.Discovery.Mdns;

/// <summary>
/// Combines PTR, SRV, TXT and A records from any number of mDNS responses into resolved
/// <see cref="MdnsServiceInstance"/> objects. Pure logic, no I/O, so it is unit-testable with
/// captured packets. Not thread-safe; the browser feeds it from a single loop.
/// </summary>
public sealed class MdnsResponseAggregator
{
    private readonly string _serviceType;
    private readonly HashSet<string> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SrvRecord> _srv = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TxtRecord> _txt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<IPAddress>> _hostAddresses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress> _responder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress?> _localAddress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _emitted = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="serviceType">Service type without trailing dot, e.g. "_axis-video._tcp.local".</param>
    public MdnsResponseAggregator(string serviceType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceType);
        _serviceType = serviceType.TrimEnd('.');
    }

    /// <summary>
    /// Feeds one response. Returns instances that became resolved or changed since they were last
    /// returned, and questions that would complete partially known instances.
    /// </summary>
    /// <param name="message">Parsed DNS message; queries are ignored.</param>
    /// <param name="responder">Source address of the packet, used when no A record is known.</param>
    /// <param name="localAddress">Local interface address the packet arrived on, if known.</param>
    public MdnsAggregationResult Process(DnsMessage message, IPAddress? responder, IPAddress? localAddress)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!message.IsResponse)
        {
            return MdnsAggregationResult.Empty;
        }

        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var flushedInThisPacket = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in message.AllRecords)
        {
            switch (record)
            {
                case PtrRecord ptr when ptr.Name.Equals(_serviceType, StringComparison.OrdinalIgnoreCase):
                    if (ptr.Ttl > 0)
                    {
                        _instances.Add(ptr.DomainName);
                        touched.Add(ptr.DomainName);
                    }

                    break;
                case SrvRecord srv when IsInstanceOfService(srv.Name):
                    _instances.Add(srv.Name);
                    _srv[srv.Name] = srv;
                    touched.Add(srv.Name);
                    break;
                case TxtRecord txt when IsInstanceOfService(txt.Name):
                    _instances.Add(txt.Name);
                    _txt[txt.Name] = txt;
                    touched.Add(txt.Name);
                    break;
                case AddressRecord { Type: DnsRecordType.A } a:
                    if (!_hostAddresses.TryGetValue(a.Name, out var list))
                    {
                        list = [];
                        _hostAddresses[a.Name] = list;
                    }

                    // Cache-flush (RFC 6762 10.2) replaces records from earlier packets only; several
                    // A records for one host in the same packet (Axis sends LAN + link-local) all stay.
                    if (a.CacheFlush && flushedInThisPacket.Add(a.Name))
                    {
                        list.Clear();
                    }

                    if (!list.Contains(a.Address))
                    {
                        list.Add(a.Address);
                    }

                    foreach (var (instance, srv) in _srv)
                    {
                        if (srv.Target.Equals(a.Name, StringComparison.OrdinalIgnoreCase))
                        {
                            touched.Add(instance);
                        }
                    }

                    break;
            }
        }

        foreach (var instance in touched)
        {
            if (responder is not null && responder.AddressFamily == AddressFamily.InterNetwork)
            {
                _responder[instance] = responder;
            }

            _localAddress[instance] = localAddress;
        }

        var resolved = new List<MdnsServiceInstance>();
        var questions = new List<DnsQuestion>();
        foreach (var instance in touched)
        {
            if (!_srv.TryGetValue(instance, out var srv))
            {
                questions.Add(new DnsQuestion(instance, DnsRecordType.Srv, false));
                if (!_txt.ContainsKey(instance))
                {
                    questions.Add(new DnsQuestion(instance, DnsRecordType.Txt, false));
                }

                continue;
            }

            if (!_hostAddresses.TryGetValue(srv.Target, out var addresses) || addresses.Count == 0)
            {
                questions.Add(new DnsQuestion(srv.Target, DnsRecordType.A, false));
                if (_responder.TryGetValue(instance, out var fallback))
                {
                    addresses = [fallback];
                }
                else
                {
                    continue;
                }
            }

            var built = Build(instance, srv, addresses);
            var signature = Signature(built);
            if (!_emitted.TryGetValue(instance, out var previous) || previous != signature)
            {
                _emitted[instance] = signature;
                resolved.Add(built);
            }
        }

        return new MdnsAggregationResult(resolved, questions.DistinctBy(q => (q.Name.ToUpperInvariant(), q.Type)).ToList());
    }

    private MdnsServiceInstance Build(string instance, SrvRecord srv, List<IPAddress> addresses)
    {
        var txt = _txt.TryGetValue(instance, out var t)
            ? t.ToDictionary()
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var host = StripLocal(srv.Target);
        var serial = SerialNumber.Normalize(txt.GetValueOrDefault("macaddress"))
            ?? SerialNumber.FromName(DisplayNameOf(instance))
            ?? SerialNumber.FromName(host);
        _localAddress.TryGetValue(instance, out var local);
        // Routable addresses first: Axis devices also announce a 169.254/16 link-local address.
        var ordered = addresses.Where(a => !IsLinkLocal(a)).Concat(addresses.Where(IsLinkLocal)).ToArray();
        return new MdnsServiceInstance(instance, host, srv.Port, ordered, txt, serial, local);
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        Span<byte> b = stackalloc byte[4];
        return address.TryWriteBytes(b, out var written) && written == 4 && b[0] == 169 && b[1] == 254;
    }

    private bool IsInstanceOfService(string name)
        => name.Length > _serviceType.Length + 1
           && name.EndsWith("." + _serviceType, StringComparison.OrdinalIgnoreCase);

    private static string DisplayNameOf(string instance)
    {
        var idx = instance.IndexOf("._", StringComparison.Ordinal);
        return idx > 0 ? instance[..idx] : instance;
    }

    private static string StripLocal(string host)
        => host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? host[..^6] : host;

    private static string Signature(MdnsServiceInstance i)
        => string.Join('|', i.HostName, i.Port, i.Serial, string.Join(',', i.Addresses.Select(a => a.ToString()).Order(StringComparer.Ordinal)));
}

/// <summary>Output of <see cref="MdnsResponseAggregator.Process"/>.</summary>
/// <param name="Resolved">Instances that are now resolved or changed.</param>
/// <param name="FollowUpQuestions">Questions to send to complete partially known instances.</param>
public sealed record MdnsAggregationResult(IReadOnlyList<MdnsServiceInstance> Resolved, IReadOnlyList<DnsQuestion> FollowUpQuestions)
{
    public static MdnsAggregationResult Empty { get; } = new([], []);
}
