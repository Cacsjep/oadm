using System.Net;
using System.Text;

using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Auth;
using Oadm.Core.MyAxis;
using Oadm.Server.MyAxis;
using Oadm.Server.Tests.Support;
using Oadm.Tests.Shared;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.MyAxis;

/// <summary>
/// MyAxisService (docs/spec/myaxis.md "gRPC"): every user, always the caller's own sign-in; checked with axis.com before it
/// is kept; audited without the session; the session never in an answer, a log or a file in clear.
/// </summary>
public sealed class MyAxisServiceTests
{
    private static async Task<(TestServerHost Host, FakeAxisCom Axis)> StartAsync()
    {
        var axis = new FakeAxisCom();
        var host = await TestServerHost.StartAsync(configureServices: s =>
        {
            s.RemoveAll<AxisComClient>();
            s.AddSingleton(new AxisComClient(axis, TimeProvider.System));
        });
        return (host, axis);
    }

    private static Proto.MyAxisService.MyAxisServiceClient ClientOf(CallInvoker invoker) => new(invoker);

    [Fact]
    public async Task EachUserSignsInAndOutOnlyTheirOwnAndTheSessionNeverComesBack()
    {
        var (host, axis) = await StartAsync();
        host.KeepDataDirectory = true;
        try
        {
            await using (host)
            {
                var admin = ClientOf(host.Invoker);
                var viewer = ClientOf(await host.InvokerForRoleAsync("viewer", BuiltInRoles.ViewerId));
                Assert.False((await viewer.GetAsync(new Proto.Empty())).SignedIn);

                var reply = await viewer.SignInAsync(new Proto.MyAxisSignInRequest { Session = FakeAxisCom.Session });

                Assert.True(reply.SignedIn);
                Assert.NotNull(reply.SignedInAt);
                Assert.False(reply.Expired);
                Assert.DoesNotContain("s3cr3t", reply.ToString(), StringComparison.Ordinal);
                Assert.Equal(3, axis.Requests.Count); // checked with axis.com first
                Assert.True((await viewer.GetAsync(new Proto.Empty())).SignedIn);
                Assert.False((await admin.GetAsync(new Proto.Empty())).SignedIn); // an Administrator sees only their own
                Assert.False((await admin.SignOutAsync(new Proto.Empty())).SignedIn);
                Assert.True((await viewer.GetAsync(new Proto.Empty())).SignedIn); // ... and never ends another user's

                Assert.False((await viewer.SignOutAsync(new Proto.Empty())).SignedIn);
                Assert.False((await viewer.GetAsync(new Proto.Empty())).SignedIn);

                var audit = (await host.Get<AuditLog>().ListAsync(100, 0, CancellationToken.None)).Entries;
                Assert.Contains(audit, e => e.Action == AuditActions.MyAxisSignedIn && e.UserName == "viewer");
                Assert.Single(audit, e => e.Action == AuditActions.MyAxisSignedOut);
                Assert.All(audit, e => Assert.DoesNotContain("s3cr3t", e.Target + e.Detail, StringComparison.Ordinal));
            }

            // Nothing in the data folder holds the session in clear: logs, settings, the database.
            foreach (var file in Directory.EnumerateFiles(host.DataDirectory, "*", SearchOption.AllDirectories))
            {
                Assert.DoesNotContain("s3cr3t", Encoding.UTF8.GetString(await ReadSharedAsync(file)), StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(host.DataDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("axis_connect_session_sid=")]
    [InlineData("other=abc")]
    [InlineData("axis_connect_session_sid=abc; other=def")]
    [InlineData("axis_connect_session_sid=a b")]
    public async Task AnythingButOneSessionPairIsInvalidAndNeverSent(string session)
    {
        var (host, axis) = await StartAsync();
        await using (host)
        {
            var error = await Assert.ThrowsAsync<RpcException>(async () =>
                await ClientOf(host.Invoker).SignInAsync(new Proto.MyAxisSignInRequest { Session = session }));

            Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
            Assert.Empty(axis.Requests);
        }
    }

    [Fact]
    public async Task ASessionAxisComRefusesIsNotKept()
    {
        var (host, axis) = await StartAsync();
        await using (host)
        {
            axis.Respond = (request, _) => Task.FromResult(request.Method == HttpMethod.Head
                ? FakeAxisCom.RedirectTo("https://eu.login.connect.axis.com/authorize")
                : FakeAxisCom.Listings(request)!);
            var client = ClientOf(host.Invoker);

            var error = await Assert.ThrowsAsync<RpcException>(async () =>
                await client.SignInAsync(new Proto.MyAxisSignInRequest { Session = FakeAxisCom.Session }));

            Assert.Equal(StatusCode.FailedPrecondition, error.StatusCode);
            Assert.Equal("MyAxis did not accept the sign-in. Try again.", error.Status.Detail);
            Assert.False((await client.GetAsync(new Proto.Empty())).SignedIn);
            Assert.DoesNotContain((await host.Get<AuditLog>().ListAsync(100, 0, CancellationToken.None)).Entries, e => e.Action == AuditActions.MyAxisSignedIn);
        }
    }

    [Fact]
    public async Task NoConnectionToAxisComIsUnavailable()
    {
        var (host, axis) = await StartAsync();
        await using (host)
        {
            axis.Respond = (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "no route");

            var error = await Assert.ThrowsAsync<RpcException>(async () =>
                await ClientOf(host.Invoker).SignInAsync(new Proto.MyAxisSignInRequest { Session = FakeAxisCom.Session }));

            Assert.Equal(StatusCode.Unavailable, error.StatusCode);
            Assert.Equal(MyAxisGrpcService.UnreachableText, error.Status.Detail);
        }
    }

    [Fact]
    public async Task AnExpiredSignInShowsAsExpiredUntilTheUserSignsInAgain()
    {
        var (host, _) = await StartAsync();
        await using (host)
        {
            var client = ClientOf(host.Invoker);
            await client.SignInAsync(new Proto.MyAxisSignInRequest { Session = FakeAxisCom.Session });
            var adminId = (await host.Auth.MeAsync(new Proto.Empty())).Id;
            await host.Get<MyAxisSessionStore>().ExpireAsync(Guid.Parse(adminId), FakeAxisCom.Session, CancellationToken.None);

            var expired = await client.GetAsync(new Proto.Empty());
            Assert.False(expired.SignedIn);
            Assert.True(expired.Expired);

            Assert.False((await client.SignInAsync(new Proto.MyAxisSignInRequest { Session = FakeAxisCom.Session })).Expired);
        }
    }

    [Fact]
    public void TheHandlerKeepsNoCookiesAndFollowsNoRedirect()
    {
        using var handler = (SocketsHttpHandler)AxisComHandler.Create();
        Assert.False(handler.UseCookies);
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void EveryMethodIsForEverySignedInUser() =>
        Assert.All(Proto.MyAxisService.Descriptor.Methods, m =>
            Assert.Equal(Auth.AccessRule.SignedIn, Auth.AccessPolicy.For($"/oadm.v1.MyAxisService/{m.Name}")));

    private static async Task<byte[]> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }
}
