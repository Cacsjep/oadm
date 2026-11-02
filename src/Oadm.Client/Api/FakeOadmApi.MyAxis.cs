using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Client.MyAxis;
using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake mode: the MyAxis sign-in of the one fake user. SignIn takes any well-formed session (nothing goes to axis.com);
/// the value "refused" is not accepted, so the window's retry and its message can be tried.
/// </summary>
public sealed partial class FakeOadmApi
{
    private MyAxisStateReply _myAxis = new();

    /// <summary>The fake's MyAxis state (tests set it, e.g. expired).</summary>
    public MyAxisStateReply FakeMyAxis
    {
        get
        {
            lock (_gate)
            {
                return _myAxis.Clone();
            }
        }

        set
        {
            lock (_gate)
            {
                _myAxis = value?.Clone() ?? new MyAxisStateReply();
            }
        }
    }

    public Task<MyAxisStateReply> GetMyAxisAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult(_myAxis.Clone());
        }
    }

    public Task<MyAxisStateReply> SignInMyAxisAsync(string session, CancellationToken ct)
    {
        const string prefix = MyAxisPages.SessionCookie + "=";
        if (session is null || !session.StartsWith(prefix, StringComparison.Ordinal) || session.Length == prefix.Length
            || MyAxisPages.SessionOf([new System.Net.Cookie(MyAxisPages.SessionCookie, session[prefix.Length..], "/", "www.axis.com")]) is null)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "The MyAxis session is not valid."));
        }

        if (session.EndsWith("=refused", StringComparison.Ordinal))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, MyAxisTexts.NotAccepted));
        }

        lock (_gate)
        {
            ThrowIfOffline();
            _myAxis = new MyAxisStateReply { SignedIn = true, SignedInAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow) };
            return Task.FromResult(_myAxis.Clone());
        }
    }

    public Task<MyAxisStateReply> SignOutMyAxisAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            _myAxis = new MyAxisStateReply();
            return Task.FromResult(_myAxis.Clone());
        }
    }
}
