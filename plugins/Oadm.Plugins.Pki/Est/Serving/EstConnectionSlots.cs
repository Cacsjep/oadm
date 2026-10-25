using System.Net;
using System.Net.Sockets;

namespace Oadm.Plugins.Est.Serving;

/// <summary>
/// The connection slots of the EST server (docs/spec/est-server.md, "Limits"): at most <see cref="MaxConnections"/> in all and
/// <see cref="MaxPerAddress"/> per address (per site: the same address at two sites is two cameras; IPv6 per /64, the
/// network one host gets). A connection counts as without a certificate until its TLS handshake showed one: at most
/// <see cref="MaxWithoutCertificate"/> of those, so hosts that never show one cannot take the cameras' slots.
/// </summary>
internal sealed class EstConnectionSlots
{
    public const int MaxConnections = 64;
    public const int MaxPerAddress = 4;
    public const int MaxWithoutCertificate = 16;

    private readonly Lock _gate = new();
    private readonly Dictionary<(Guid? Site, IPAddress Address), int> _perAddress = [];
    private int _open;
    private int _withoutCertificate;

    public int Open
    {
        get
        {
            lock (_gate)
            {
                return _open;
            }
        }
    }

    public int WithoutCertificate
    {
        get
        {
            lock (_gate)
            {
                return _withoutCertificate;
            }
        }
    }

    /// <summary>A slot for a new connection (without a certificate yet), or null when the limits are reached.</summary>
    public Slot? TryEnter(Guid? site, IPAddress address)
    {
        var key = (site, KeyOf(address));
        lock (_gate)
        {
            if (_open >= MaxConnections || _withoutCertificate >= MaxWithoutCertificate || _perAddress.GetValueOrDefault(key) >= MaxPerAddress)
            {
                return null;
            }

            _open++;
            _withoutCertificate++;
            _perAddress[key] = _perAddress.GetValueOrDefault(key) + 1;
            return new Slot(this, key);
        }
    }

    /// <summary>The address a slot counts for: an IPv4 address as is (also when mapped into IPv6), an IPv6 address by its /64.</summary>
    internal static IPAddress KeyOf(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes);
    }

    private void Identified(Slot slot)
    {
        lock (_gate)
        {
            if (slot.WithoutCertificate)
            {
                slot.WithoutCertificate = false;
                _withoutCertificate--;
            }
        }
    }

    private void Leave(Slot slot)
    {
        lock (_gate)
        {
            if (slot.Left)
            {
                return;
            }

            slot.Left = true;
            _open--;
            if (slot.WithoutCertificate)
            {
                _withoutCertificate--;
            }

            if (--_perAddress[slot.Key] <= 0)
            {
                _perAddress.Remove(slot.Key);
            }
        }
    }

    /// <summary>One connection's slot; disposing it gives it back.</summary>
    internal sealed class Slot(EstConnectionSlots owner, (Guid? Site, IPAddress Address) key) : IDisposable
    {
        public (Guid? Site, IPAddress Address) Key { get; } = key;

        internal bool WithoutCertificate { get; set; } = true;

        internal bool Left { get; set; }

        /// <summary>The handshake showed a client certificate: the slot no longer counts as without one.</summary>
        public void Identified() => owner.Identified(this);

        public void Dispose() => owner.Leave(this);
    }
}
