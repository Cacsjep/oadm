using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Grpc.Core;

using Oadm.Contracts.SiteNode;
using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Identity;
using Oadm.SiteNode.Links;
using Oadm.Sites.Binding;
using Oadm.Sites.Transport;

namespace Oadm.SiteNode.State;

/// <summary>
/// The node's states (src/Oadm.SiteNode/README.md, "States"): unbound with binding open (24 hours of run time, counted by
/// <see cref="Tick"/> and saved every minute), unbound with binding closed, bound to one server. The pairing code lives in
/// memory only (a new one at every start, every <see cref="CodeLifetime"/>, Allow binding, Reset, Unbind and after 5 wrong
/// codes). Every local user may read it while it is shown: it lasts minutes only and the node binds only to the server
/// an administrator saved.
/// </summary>
public sealed class BindingState
{
    public const int MaxWrongCodes = 5;
    public const int WrongCodesKept = 20;
    public static readonly TimeSpan LockTime = TimeSpan.FromMinutes(10);

    /// <summary>A pairing code is replaced by a new one after this time.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>A pairing that started with a code just replaced by time still binds with it for this long.</summary>
    public static readonly TimeSpan ExpiredCodeGrace = TimeSpan.FromMinutes(1);

    /// <summary>PairStart attempts a minute from one address (IPv6 per /64) and from all addresses.</summary>
    public const int PairAttemptsPerAddress = 10;
    public const int PairAttemptsTotal = 30;

    /// <summary>A new identity the app asks about is not replaced by another one for this long (and never once accepted).</summary>
    public static readonly TimeSpan NewIdentityHold = TimeSpan.FromHours(1);

    private readonly NodeStore _store;
    private readonly NodeIdentity _identity;
    private readonly TimeProvider _time;
    private readonly string _hostName;
    private readonly AddressRateLimiter _attempts;
    private readonly Lock _checkLock = new();
    private readonly Lock _lock = new();
    private readonly List<WrongCodeView> _wrongCodes = [];
    private string _code = Oadm.Sites.Binding.PairingCode.Create();
    private long _codeGeneration = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue); // random per run: a restarted service never repeats the last one
    private int _wrongCount;
    private DateTimeOffset? _lockedUntil;
    private long _lastTick;
    private long _codeStarted;
    private string? _expiredCode;
    private long _expiredAt;

    public BindingState(NodeStore store, NodeIdentity identity, TimeProvider time, string hostName)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(time);
        _store = store;
        _identity = identity;
        _time = time;
        _hostName = hostName;
        _attempts = new AddressRateLimiter(PairAttemptsPerAddress, PairAttemptsTotal, TimeSpan.FromMinutes(1), time);
        _lastTick = time.GetTimestamp();
        _codeStarted = _lastTick;
    }

    /// <summary>Raised after every change of state, code or attempts (the store raises its own for node.json).</summary>
    public event Action? Changed;

    /// <summary>Raised after Reset: links with the old identity must close.</summary>
    public event Action? IdentityReset;

    public NodeBinding Binding => _store.Read(Of);

    public BoundServer? Bound => _store.Read(c => c.BoundServers.Count > 0 ? c.BoundServers[0] : null);

    /// <summary>The current pairing code (normalized, without the dash). Never sent to the server.</summary>
    public string PairingCode => CurrentCode().Code;

    /// <summary>
    /// The current pairing code, when it is replaced by a new one and its generation, read together (a code past its time
    /// is replaced first). The grace of a pairing started with the old code counts from the old code's scheduled end, not
    /// from this read.
    /// </summary>
    public (string Code, DateTimeOffset ValidUntil, long Generation) CurrentCode()
    {
        var now = _time.GetTimestamp();
        bool rotated;
        (string, DateTimeOffset, long) current;
        lock (_lock)
        {
            var lifetime = (long)(CodeLifetime.TotalSeconds * _time.TimestampFrequency);
            rotated = _time.GetElapsedTime(_codeStarted, now) >= CodeLifetime;
            if (rotated)
            {
                _expiredCode = _code;
                _expiredAt = _codeStarted + lifetime;
                _code = Sites.Binding.PairingCode.Create();
                _codeGeneration++;
                _codeStarted = now;
            }

            current = (_code, _time.GetUtcNow() + CodeLifetime - _time.GetElapsedTime(_codeStarted, now), _codeGeneration);
        }

        if (rotated)
            NodeEvents.Raise(Changed);
        return current;
    }

    /// <summary>
    /// Changes with every new pairing code (and differs after a service restart); never the code. The app forgets a code it
    /// shows once this changes.
    /// </summary>
    public long PairingCodeGeneration => CurrentCode().Generation;

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
        _store.TryUpdate(c =>
        {
            if (c.BoundServers.Count > 0 || c.BindingSecondsLeft <= 0)
                return false;
            c.BindingSecondsLeft = Math.Max(0, c.BindingSecondsLeft - elapsed.TotalSeconds);
            return true;
        });
        if (before != Binding)
            NodeEvents.Raise(Changed);
    }

    /// <summary>Allow binding: another 24 hours and a new code (only while not bound).</summary>
    public void AllowBinding()
    {
        _store.TryUpdate(c =>
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
            ClearNewIdentity(c);
            c.Requested.Clear();
        });
        NewCode(clearAttempts: true);
        NodeEvents.Raise(IdentityReset);
    }

    /// <summary>True while a server with <paramref name="fingerprintHex"/> may pair: binding open, or its new identity accepted in the app.</summary>
    public bool IsPairingOpenFor(string fingerprintHex) =>
        _store.Read(c => c.BoundServers.Count == 0
            ? c.BindingSecondsLeft > 0
            : c.NewServerAccepted && string.Equals(c.NewServerIdentity, fingerprintHex, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True while every local user may read the pairing code (decision 2026-10-21): binding open on a node that is not bound.
    /// A bound node that accepted a new server identity hands its code to administrators only.
    /// </summary>
    public bool ShowsPairingCodeToEveryone => Binding == NodeBinding.Open;

    /// <summary>True while the app shows a pairing code: binding open, or a new server identity accepted.</summary>
    public bool ShowsPairingCode => _store.Read(c => c.BoundServers.Count == 0 ? c.BindingSecondsLeft > 0 : c.NewServerAccepted && c.NewServerIdentity.Length > 0);

    /// <summary>
    /// PairStart on <paramref name="link"/>: a fresh J-PAKE participant for the current code; checks the server's round 1
    /// and returns the node's round 1 and round 2. Refused while closed or locked and over the attempt limits (per address
    /// and in all; a Bind needs a PairStart of its own link, so this limits Bind too).
    /// </summary>
    public (PakeRound1 Round1, PakeRound2 Round2) StartPairing(SiteLink link, PakeRound1? serverRound1)
    {
        ArgumentNullException.ThrowIfNull(link);
        RefuseUnlessOpen(link);
        lock (_lock)
        {
            if (IsLocked())
                throw new NodeRefusalException(StatusCode.ResourceExhausted, AdminTexts.TooManyWrongCodes);
        }

        if (!_attempts.TryAcquire(link.RemoteKey))
            throw new NodeRefusalException(StatusCode.ResourceExhausted, AdminTexts.TooManyPairingAttempts);
        var code = PairingCode;
        var pairing = new NodePairing(code);
        var rounds = pairing.Start(serverRound1) ?? throw new NodeRefusalException(StatusCode.InvalidArgument, AdminTexts.InvalidPairingValue);
        link.SetPairing(pairing, code);
        return rounds;
    }

    /// <summary>
    /// Bind on <paramref name="link"/>: the node's proof, or a refusal. A failed proof counts as a wrong code. Proofs are
    /// checked one at a time with the lock state checked under the same lock, so parallel links never get more than
    /// <see cref="MaxWrongCodes"/> guesses before the lock.
    /// </summary>
    public byte[] Bind(SiteLink link, string siteName, string serverName, PakeRound2? serverRound2, byte[] proof)
    {
        ArgumentNullException.ThrowIfNull(link);
        RefuseUnlessOpen(link);
        var (pairing, code) = link.TakePairing();
        if (pairing is null)
            throw new NodeRefusalException(AdminTexts.PairStartFirst);

        byte[] reply;
        lock (_checkLock)
        {
            RefuseUnlessOpen(link); // again: another link may have bound meanwhile
            lock (_lock)
            {
                if (IsLocked())
                    throw new NodeRefusalException(StatusCode.ResourceExhausted, AdminTexts.TooManyWrongCodes);
            }

            // A code made new meanwhile (5 wrong codes, Allow binding) makes an attempt for the old one a wrong code; one
            // replaced by time a moment ago still counts (the pairing started with it).
            var answer = string.Equals(code, PairingCode, StringComparison.Ordinal) || IsJustExpired(code)
                ? pairing.Finish(serverRound2, proof ?? [], link.NodeFingerprint, link.ServerFingerprint)
                : null;
            reply = answer ?? throw WrongCode(link.RemoteHost);
            lock (_lock)
            {
                _wrongCount = 0;
                _lockedUntil = null;
            }
        }

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
            ClearNewIdentity(c);
            c.BindingSecondsLeft = 0;
        });
        NewCode(clearAttempts: true);
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
            ClearNewIdentity(c);
        });
        NewCode(clearAttempts: true);
    }

    public void Rename(string siteName) =>
        _store.TryUpdate(c =>
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

        _store.TryUpdate(c =>
        {
            if (c.BoundServers.Count == 0)
                return false;
            c.BoundServers[0].Fingerprint = fingerprint;
            if (string.Equals(c.NewServerIdentity, fingerprint, StringComparison.OrdinalIgnoreCase))
                ClearNewIdentity(c);
            return true;
        });
    }

    /// <summary>
    /// A server at one of the bound server's addresses presented another identity: the app asks about it, naming where it
    /// came from. A pending one is kept (never replaced silently): another identity takes its place only after
    /// <see cref="NewIdentityHold"/> and never once accepted. Returns false when the identity was not noted.
    /// </summary>
    public bool NoteNewServerIdentity(string fingerprintHex, string from)
    {
        ArgumentNullException.ThrowIfNull(fingerprintHex);
        var now = _time.GetUtcNow();
        var noted = false;
        _store.TryUpdate(c =>
        {
            if (c.BoundServers.Count == 0 || string.Equals(c.BoundServers[0].Fingerprint, fingerprintHex, StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.Equals(c.NewServerIdentity, fingerprintHex, StringComparison.OrdinalIgnoreCase))
            {
                noted = true;
                return false;
            }

            if (c.NewServerIdentity.Length > 0 && (c.NewServerAccepted || c.NewServerSince is not { } since || now - since < NewIdentityHold))
                return false;
            c.NewServerIdentity = fingerprintHex;
            c.NewServerFrom = Clean(from);
            c.NewServerSince = now;
            c.NewServerAccepted = false;
            noted = true;
            return true;
        });
        if (noted)
            NodeEvents.Raise(Changed);
        return noted;
    }

    /// <summary>
    /// Accept (administrator): the server with the new identity may now bind with the pairing code, as if binding were open
    /// for it alone. Nothing changes until it proves the code, so a stranger that raised the request cannot take the node.
    /// Returns the pairing code to show.
    /// </summary>
    public string AcceptNewServerIdentity()
    {
        var accepted = _store.TryUpdate(c =>
        {
            if (c.BoundServers.Count == 0 || string.IsNullOrEmpty(c.NewServerIdentity))
                return false;
            c.NewServerAccepted = true;
            return true;
        });
        if (!accepted)
            throw new NodeRefusalException(AdminTexts.NoNewIdentity);
        NewCode(clearAttempts: false);
        return PairingCode;
    }

    private static void ClearNewIdentity(NodeConfig c)
    {
        c.NewServerIdentity = "";
        c.NewServerFrom = "";
        c.NewServerSince = null;
        c.NewServerAccepted = false;
    }

    private static NodeBinding Of(NodeConfig c) =>
        c.BoundServers.Count > 0 ? NodeBinding.Bound : c.BindingSecondsLeft > 0 ? NodeBinding.Open : NodeBinding.Closed;

    private void RefuseUnlessOpen(SiteLink link)
    {
        switch (Binding)
        {
            case NodeBinding.Bound when IsPairingOpenFor(link.ServerFingerprintHex):
                return; // the administrator accepted this server's new identity: it binds with the code
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
                NewCodeLocked();
            }
        }

        NodeEvents.Raise(Changed);
        return locked
            ? new NodeRefusalException(StatusCode.ResourceExhausted, AdminTexts.TooManyWrongCodes)
            : new NodeRefusalException(StatusCode.PermissionDenied, AdminTexts.WrongCode);
    }

    private bool IsJustExpired(string? code)
    {
        lock (_lock)
            return _expiredCode is { } expired && string.Equals(code, expired, StringComparison.Ordinal)
                && _time.GetElapsedTime(_expiredAt) < ExpiredCodeGrace;
    }

    private bool IsLocked() => _lockedUntil is { } until && _time.GetUtcNow() < until;

    private void NewCode(bool clearAttempts)
    {
        lock (_lock)
        {
            NewCodeLocked();
            if (clearAttempts)
            {
                _wrongCount = 0;
                _lockedUntil = null;
            }
        }

        NodeEvents.Raise(Changed);
    }

    private void NewCodeLocked()
    {
        _code = Sites.Binding.PairingCode.Create();
        _codeGeneration++;
        _codeStarted = _time.GetTimestamp();
        _expiredCode = null;
    }

    private static string Clean(string? text)
    {
        var value = new string([.. (text ?? "").Where(ch => !char.IsControl(ch))]).Trim();
        return value.Length > 64 ? value[..64] : value;
    }
}
