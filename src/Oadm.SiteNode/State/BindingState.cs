using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Grpc.Core;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Identity;
using Oadm.SiteNode.Links;
using Oadm.Sites.Binding;

namespace Oadm.SiteNode.State;

/// <summary>
/// The node's states (docs/sites.md, "States" and "Binding"): unbound with binding open (24 hours of run time, counted by
/// <see cref="Tick"/> and saved every minute), unbound with binding closed, bound to one server. The pairing code lives in
/// memory only (a new one at every start, Allow binding, Reset, Unbind and after 5 wrong codes).
/// </summary>
public sealed class BindingState
{
    public const int MaxWrongCodes = 5;
    public const int WrongCodesKept = 20;
    public static readonly TimeSpan LockTime = TimeSpan.FromMinutes(10);

    private readonly NodeStore _store;
    private readonly NodeIdentity _identity;
    private readonly TimeProvider _time;
    private readonly string _hostName;
    private readonly Lock _lock = new();
    private readonly List<WrongCodeView> _wrongCodes = [];
    private string _code = Oadm.Sites.Binding.PairingCode.Create();
    private int _wrongCount;
    private DateTimeOffset? _lockedUntil;
    private long _lastTick;

    public BindingState(NodeStore store, NodeIdentity identity, TimeProvider time, string hostName)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _identity = identity;
        _time = time;
        _hostName = hostName;
        _lastTick = time.GetTimestamp();
    }

    /// <summary>Raised after every change of state, code or attempts (the store raises its own for node.json).</summary>
    public event Action? Changed;

    /// <summary>Raised after Reset: links with the old identity must close.</summary>
    public event Action? IdentityReset;

    public NodeBinding Binding => _store.Read(Of);

    public BoundServer? Bound => _store.Read(c => c.BoundServers.Count > 0 ? c.BoundServers[0] : null);

    /// <summary>The current pairing code (normalized, without the dash). Never sent to the server.</summary>
    public string PairingCode
    {
        get
        {
            lock (_lock)
                return _code;
        }
    }

    public DateTimeOffset? LockedUntil
    {
        get
        {
            lock (_lock)
                return IsLocked() ? _lockedUntil : null;
        }
    }

    public IReadOnlyList<WrongCodeView> WrongCodes
    {
        get
        {
            lock (_lock)
                return [.. _wrongCodes];
        }
    }

    public TimeSpan BindingLeft => TimeSpan.FromSeconds(_store.Read(c => c.BoundServers.Count > 0 ? 0 : Math.Max(0, c.BindingSecondsLeft)));

    /// <summary>True when <paramref name="fingerprintHex"/> is the bound server's identity.</summary>
    public bool IsBoundTo(string fingerprintHex) =>
        _store.Read(c => c.BoundServers.Exists(s => string.Equals(s.Fingerprint, fingerprintHex, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Counts the run time since the last tick against the binding window and saves it (the service calls it every minute).</summary>
    public void Tick()
    {
        var now = _time.GetTimestamp();
        TimeSpan elapsed;
        lock (_lock)
        {
            elapsed = _time.GetElapsedTime(_lastTick, now);
            _lastTick = now;
        }

        var before = Binding;
        _store.Update(c =>
        {
            if (c.BoundServers.Count > 0 || c.BindingSecondsLeft <= 0)
                return false;
            c.BindingSecondsLeft = Math.Max(0, c.BindingSecondsLeft - elapsed.TotalSeconds);
            return true;
        });
        if (before != Binding)
            Changed?.Invoke();
    }

    /// <summary>Allow binding: another 24 hours and a new code (only while not bound).</summary>
    public void AllowBinding()
    {
        _store.Update(c =>
        {
            if (c.BoundServers.Count > 0)
                return false;
            c.BindingSecondsLeft = NodeConfig.BindingWindow.TotalSeconds;
            return true;
        });
        NewCode(clearAttempts: true);
    }

    /// <summary>Reset: a new id, key and code, the server forgotten, binding open for 24 hours.</summary>
    public void Reset()
    {
        _identity.Renew(_hostName);
        _store.Update(c =>
        {
            c.Id = NodeStore.NewId();
            c.BoundServers.Clear();
            c.BindingSecondsLeft = NodeConfig.BindingWindow.TotalSeconds;
            c.ServerAddresses = new ServerAddressList();
            c.NewServerIdentity = "";
            c.Requested.Clear();
        });
        NewCode(clearAttempts: true);
        IdentityReset?.Invoke();
    }

    /// <summary>PairStart on <paramref name="link"/>: the salt and B of the current code.</summary>
    public (byte[] Salt, byte[] B) StartPairing(SiteLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        RefuseUnlessOpen(link);
        var code = PairingCode;
        var pairing = new NodePairing(code);
        link.SetPairing(pairing, code);
        return pairing.Start();
    }

    /// <summary>Bind on <paramref name="link"/>: the node's proof, or a refusal. A failed proof counts as a wrong code.</summary>
    public byte[] Bind(SiteLink link, string siteName, string serverName, byte[] a, byte[] proof)
    {
        ArgumentNullException.ThrowIfNull(link);
        RefuseUnlessOpen(link);
        lock (_lock)
        {
            if (IsLocked())
                throw new NodeRefusalException(StatusCode.ResourceExhausted, AdminTexts.TooManyWrongCodes);
        }

        var (pairing, code) = link.TakePairing();
        if (pairing is null)
            throw new NodeRefusalException(AdminTexts.PairStartFirst);
        var reply = string.Equals(code, PairingCode, StringComparison.Ordinal)
            ? pairing.Finish(a ?? [], proof ?? [], link.NodeFingerprint, link.ServerFingerprint)
            : null;
        if (reply is null)
            throw WrongCode(link.RemoteHost);

        _store.Update(c =>
        {
            c.BoundServers.Clear();
            c.BoundServers.Add(new BoundServer
            {
                Name = Clean(serverName),
                Fingerprint = link.ServerFingerprintHex,
                BoundUtc = _time.GetUtcNow(),
                SiteName = Clean(siteName),
            });
            c.NewServerIdentity = "";
            c.BindingSecondsLeft = 0;
        });
        lock (_lock)
        {
            _wrongCount = 0;
            _lockedUntil = null;
        }

        Changed?.Invoke();
        return reply;
    }

    /// <summary>The bound server forgets the node: binding opens again (24 hours, a new code), the identity stays.</summary>
    public void Unbind()
    {
        _store.Update(c =>
        {
            c.BoundServers.Clear();
            c.BindingSecondsLeft = NodeConfig.BindingWindow.TotalSeconds;
            c.ServerAddresses = new ServerAddressList();
            c.NewServerIdentity = "";
        });
        NewCode(clearAttempts: true);
    }

    public void Rename(string siteName) =>
        _store.Update(c =>
        {
            if (c.BoundServers.Count == 0)
                return false;
            c.BoundServers[0].SiteName = Clean(siteName);
            return true;
        });

    /// <summary>RotateIdentity: a new server certificate signed with the key of the certificate on this link.</summary>
    public void RotateIdentity(SiteLink link, byte[] newCertificate, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(link);
        string fingerprint;
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(newCertificate);
            using var oldKey = link.ServerCertificate.GetECDsaPublicKey();
            if (oldKey is null || !oldKey.VerifyData(newCertificate, signature, HashAlgorithmName.SHA256))
                throw new NodeRefusalException(StatusCode.InvalidArgument, AdminTexts.InvalidSignature);
            fingerprint = Convert.ToHexString(SHA256.HashData(newCertificate));
        }
        catch (CryptographicException e)
        {
            throw new NodeRefusalException(StatusCode.InvalidArgument, AdminTexts.InvalidSignature + " " + e.Message);
        }

        _store.Update(c =>
        {
            if (c.BoundServers.Count == 0)
                return false;
            c.BoundServers[0].Fingerprint = fingerprint;
            return true;
        });
    }

    /// <summary>The bound server (by its address) presented another identity: the app asks to accept it.</summary>
    public void NoteNewServerIdentity(string fingerprintHex) =>
        _store.Update(c =>
        {
            if (c.BoundServers.Count == 0 || string.Equals(c.NewServerIdentity, fingerprintHex, StringComparison.Ordinal))
                return false;
            c.NewServerIdentity = fingerprintHex;
            return true;
        });

    /// <summary>Accept (administrator): the new identity becomes the pinned one.</summary>
    public void AcceptNewServerIdentity()
    {
        var accepted = _store.Update(c =>
        {
            if (c.BoundServers.Count == 0 || string.IsNullOrEmpty(c.NewServerIdentity))
                return false;
            c.BoundServers[0].Fingerprint = c.NewServerIdentity;
            c.NewServerIdentity = "";
            return true;
        });
        if (!accepted)
            throw new NodeRefusalException(AdminTexts.NoNewIdentity);
    }

    private static NodeBinding Of(NodeConfig c) =>
        c.BoundServers.Count > 0 ? NodeBinding.Bound : c.BindingSecondsLeft > 0 ? NodeBinding.Open : NodeBinding.Closed;

    private void RefuseUnlessOpen(SiteLink link)
    {
        switch (Binding)
        {
            case NodeBinding.Bound:
                throw new NodeRefusalException(IsBoundTo(link.ServerFingerprintHex) ? AdminTexts.AlreadyBound : AdminTexts.BoundToAnotherServer);
            case NodeBinding.Closed:
                throw new NodeRefusalException(AdminTexts.BindingClosed);
        }
    }

    private NodeRefusalException WrongCode(string from)
    {
        bool locked;
        lock (_lock)
        {
            _wrongCodes.Add(new WrongCodeView(_time.GetUtcNow(), from));
            if (_wrongCodes.Count > WrongCodesKept)
                _wrongCodes.RemoveAt(0);
            _wrongCount++;
            locked = _wrongCount >= MaxWrongCodes;
            if (locked)
            {
                _wrongCount = 0;
                _lockedUntil = _time.GetUtcNow() + LockTime;
                _code = Sites.Binding.PairingCode.Create();
            }
        }

        Changed?.Invoke();
        return locked
            ? new NodeRefusalException(StatusCode.ResourceExhausted, AdminTexts.TooManyWrongCodes)
            : new NodeRefusalException(StatusCode.PermissionDenied, AdminTexts.WrongCode);
    }

    private bool IsLocked() => _lockedUntil is { } until && _time.GetUtcNow() < until;

    private void NewCode(bool clearAttempts)
    {
        lock (_lock)
        {
            _code = Sites.Binding.PairingCode.Create();
            if (clearAttempts)
            {
                _wrongCount = 0;
                _lockedUntil = null;
            }
        }

        Changed?.Invoke();
    }

    private static string Clean(string? text)
    {
        var value = new string([.. (text ?? "").Where(ch => !char.IsControl(ch))]).Trim();
        return value.Length > 64 ? value[..64] : value;
    }
}
