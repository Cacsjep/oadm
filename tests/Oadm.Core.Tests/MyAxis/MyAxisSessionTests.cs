using System.Net;
using System.Text;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Auth;
using Oadm.Core.Backup;
using Oadm.Core.MyAxis;
using Oadm.Core.Persistence;
using Oadm.Core.Security;
using Oadm.Core.Tests.Persistence;
using Oadm.Tests.Shared;

namespace Oadm.Core.Tests.MyAxis;

/// <summary>
/// The MyAxis session rules (docs/spec/myaxis.md): one pair only, per user, encrypted with the user id as associated data,
/// gone with the user and never in a backup; the sign-in check sends it only to the download folders and follows no redirect.
/// </summary>
public sealed class MyAxisSessionTests
{
    private static MyAxisSessionStore StoreOf(TestDatabase db, TimeProvider? time = null) =>
        new(db.Get<IDbContextFactory<OadmDbContext>>(), db.Get<CredentialProtector>(), time ?? TimeProvider.System);

    private static async Task<Guid> AddUserAsync(TestDatabase db, string name, Guid? role = null) =>
        (await db.Get<UserStore>().AddAsync(name, "a-long-password-1", role ?? BuiltInRoles.OperatorId, CancellationToken.None)).Id;

    [Theory]
    [InlineData("axis_connect_session_sid=abc123", true)]
    [InlineData("axis_connect_session_sid=a-b_c.d~e!%7B", true)]
    [InlineData("axis_connect_session_sid=", false)]
    [InlineData("axis_connect_session_sid", false)]
    [InlineData("other=abc", false)]
    [InlineData("token=abc", false)]
    [InlineData("axis_connect_session_sid=abc; token=x", false)]
    [InlineData("axis_connect_session_sid=ab c", false)]
    [InlineData("axis_connect_session_sid=ab,c", false)]
    [InlineData("axis_connect_session_sid=ab\"c", false)]
    [InlineData("axis_connect_session_sid=ab\\c", false)]
    [InlineData("axis_connect_session_sid=abé", false)]
    [InlineData("axis_connect_session_sid=ab\r\nX-Evil: 1", false)]
    [InlineData(" axis_connect_session_sid=abc", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyOneSessionPairIsAccepted(string? session, bool valid) => Assert.Equal(valid, MyAxisCookies.IsValid(session));

    [Fact]
    public void ASessionIsAtMost4096Characters()
    {
        const string prefix = "axis_connect_session_sid=";
        Assert.True(MyAxisCookies.IsValid(prefix + new string('a', 4096 - prefix.Length)));
        Assert.False(MyAxisCookies.IsValid(prefix + new string('a', 4097 - prefix.Length)));
    }

    [Theory]
    [InlineData("https://www.axis.com/ftp/pub_soft/MPQT/AXIS_P3265/12_6_1/P3265_12_6_1.bin", true)]
    [InlineData("https://www.axis.com/ftp/pub_soft/PACS/AXIS_A1601/latest/A1601.bin", true)]
    [InlineData("https://WWW.AXIS.COM/ftp/pub_soft/MPQT/a.bin", true)]
    [InlineData("https://www.axis.com/ftp/pub_soft/MPQT/", false)]
    [InlineData("http://www.axis.com/ftp/pub_soft/MPQT/a.bin", false)]
    [InlineData("https://axis.com/ftp/pub_soft/MPQT/a.bin", false)]
    [InlineData("https://www.axis.com.evil.example/ftp/pub_soft/MPQT/a.bin", false)]
    [InlineData("https://www.axis.com:8443/ftp/pub_soft/MPQT/a.bin", false)]
    [InlineData("https://user@www.axis.com/ftp/pub_soft/MPQT/a.bin", false)]
    [InlineData("https://www.axis.com/ftp/pub_soft/MPQT/../secret/a.bin", false)]
    [InlineData("https://www.axis.com/ftp/pub_soft/MPQT/%2e%2e/a.bin", false)]
    [InlineData("https://www.axis.com/ftp/pub_soft/MPQT/a%2f..%2fb.bin", false)]
    [InlineData("https://www.axis.com/ftp/pub_soft/MPQT/a.bin?x=1", false)]
    [InlineData("https://www.axis.com/ftp/pub_soft/MPQT/a.bin#x", false)]
    [InlineData("https://www.axis.com/ftp/pub_soft/OTHER/a.bin", false)]
    [InlineData("https://www.axis.com/en-us/my-axis", false)]
    public void OnlyTheTwoDownloadFoldersOfWwwAxisComAreAllowed(string url, bool allowed) =>
        Assert.Equal(allowed, AxisDownloadUrls.IsAllowed(new Uri(url)));

    [Fact]
    public async Task TheSignInCheckAsksForTheHeadersOfTheFirstProductsLatestFileWithTheSessionOnly()
    {
        var axis = new FakeAxisCom();
        using var client = new AxisComClient(axis, TimeProvider.System);

        Assert.Equal(AxisAnswer.Ok, await client.CheckAsync(FakeAxisCom.Session, CancellationToken.None));

        var requests = axis.Requests;
        Assert.Equal(3, requests.Count);
        Assert.Equal(AxisDownloadUrls.MpqtListing, requests[0].Uri);
        Assert.Equal(new Uri(FakeAxisCom.Product, "latest/"), requests[1].Uri);
        Assert.Equal((HttpMethod.Head, FakeAxisCom.ProbeFile), (requests[2].Method, requests[2].Uri));
        Assert.All(requests, r => Assert.Equal(FakeAxisCom.Session, r.Cookie)); // the one Cookie header, exactly as kept
        Assert.All(requests, r => Assert.True(AxisDownloadUrls.IsAllowed(r.Uri) || r.Uri == AxisDownloadUrls.MpqtListing));
    }

    [Theory]
    [InlineData("https://eu.login.connect.axis.com/authorize?x=1")]
    [InlineData("https://auth.axis.com/oauth2")]
    [InlineData("https://login.axis.com/")]
    [InlineData("/en-us/my-axis/login?axis_destination=x")]
    public async Task ARedirectToALoginPageIsNotAcceptedAndNotFollowed(string location)
    {
        var axis = new FakeAxisCom();
        axis.Respond = (request, _) => Task.FromResult(request.Method == HttpMethod.Head
            ? FakeAxisCom.RedirectTo(location)
            : FakeAxisCom.Listings(request)!);
        using var client = new AxisComClient(axis, TimeProvider.System);

        Assert.Equal(AxisAnswer.SignInNeeded, await client.CheckAsync(FakeAxisCom.Session, CancellationToken.None));
        Assert.Equal(3, axis.Requests.Count); // the login page was never asked
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, AxisAnswer.SignInNeeded)]
    [InlineData(HttpStatusCode.Unauthorized, AxisAnswer.SignInNeeded)]
    [InlineData(HttpStatusCode.NotFound, AxisAnswer.Failed)]
    [InlineData(HttpStatusCode.MethodNotAllowed, AxisAnswer.Failed)]
    public async Task TheProbeFilesAnswerDecides(HttpStatusCode code, AxisAnswer expected)
    {
        var axis = new FakeAxisCom();
        axis.Respond = (request, _) => Task.FromResult(request.Method == HttpMethod.Head ? new HttpResponseMessage(code) : FakeAxisCom.Listings(request)!);
        using var client = new AxisComClient(axis, TimeProvider.System);

        Assert.Equal(expected, await client.CheckAsync(FakeAxisCom.Session, CancellationToken.None));
    }

    [Fact]
    public void ARedirectElsewhereIsNoSignInPage()
    {
        Assert.False(AxisComClient.IsLoginPage(new Uri("https://cdn.axis.com/ftp/pub_soft/MPQT/a.bin")));
        Assert.False(AxisComClient.IsLoginPage(null));
    }

    [Fact]
    public async Task SessionsArePerUserEncryptedWithTheUserIdAndShowNoSecret()
    {
        await using var db = await TestDatabase.CreateAsync();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
        var store = StoreOf(db, time);
        var anna = await AddUserAsync(db, "anna");
        var ben = await AddUserAsync(db, "ben", BuiltInRoles.AdministratorId);

        await store.SaveAsync(anna, FakeAxisCom.Session, CancellationToken.None);

        Assert.Equal(new MyAxisState(true, time.GetUtcNow().UtcDateTime, false), await store.GetStateAsync(anna, CancellationToken.None));
        Assert.Equal(MyAxisState.None, await store.GetStateAsync(ben, CancellationToken.None)); // an Administrator included
        Assert.Null(await store.GetSessionAsync(ben, CancellationToken.None));
        Assert.Equal(FakeAxisCom.Session, await store.GetSessionAsync(anna, CancellationToken.None));

        await using (var context = await db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
        {
            var row = await context.MyAxisSessions.SingleAsync();
            Assert.DoesNotContain("s3cr3t", Encoding.UTF8.GetString(row.EncryptedSession!), StringComparison.Ordinal);
            Assert.DoesNotContain("s3cr3t", row.ToString(), StringComparison.Ordinal);

            // The blob copied to another user's row does not decrypt there.
            context.MyAxisSessions.Add(new MyAxisSessionEntity { UserId = ben, EncryptedSession = row.EncryptedSession, SignedInUtc = row.SignedInUtc, LastAcceptedUtc = row.LastAcceptedUtc });
            await context.SaveChangesAsync();
        }

        Assert.Null(await store.GetSessionAsync(ben, CancellationToken.None));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(db.Paths.DataDirectory, "oadm.db*"))
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var raw = new MemoryStream();
            await stream.CopyToAsync(raw);
            Assert.DoesNotContain("s3cr3t", Encoding.UTF8.GetString(raw.ToArray()), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExpiryDeletesTheSessionAndSaysSoUntilSignInOrSignOut()
    {
        await using var db = await TestDatabase.CreateAsync();
        var store = StoreOf(db);
        var anna = await AddUserAsync(db, "anna");
        await store.SaveAsync(anna, FakeAxisCom.Session, CancellationToken.None);

        await store.ExpireAsync(anna, "axis_connect_session_sid=an-older-one", CancellationToken.None);
        Assert.True((await store.GetStateAsync(anna, CancellationToken.None)).SignedIn); // a newer session stays

        await store.ExpireAsync(anna, FakeAxisCom.Session, CancellationToken.None);
        Assert.Equal(new MyAxisState(false, null, true), await store.GetStateAsync(anna, CancellationToken.None));
        Assert.Null(await store.GetSessionAsync(anna, CancellationToken.None));
        Assert.False(await store.HasSessionAsync(anna, CancellationToken.None));

        await store.SaveAsync(anna, FakeAxisCom.Session, CancellationToken.None);
        Assert.False((await store.GetStateAsync(anna, CancellationToken.None)).Expired);
        Assert.True(await store.DeleteAsync(anna, CancellationToken.None));
        Assert.Equal(MyAxisState.None, await store.GetStateAsync(anna, CancellationToken.None));
        Assert.False(await store.DeleteAsync(anna, CancellationToken.None));
    }

    [Fact]
    public async Task ADeletedUsersSessionGoesWithTheUser()
    {
        await using var db = await TestDatabase.CreateAsync();
        var store = StoreOf(db);
        var admin = await AddUserAsync(db, "admin", BuiltInRoles.AdministratorId);
        var anna = await AddUserAsync(db, "anna");
        await store.SaveAsync(anna, FakeAxisCom.Session, CancellationToken.None);
        await store.SaveAsync(admin, FakeAxisCom.Session, CancellationToken.None);

        await db.Get<UserStore>().DeleteAsync(anna, admin, CancellationToken.None);

        await using var context = await db.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync();
        Assert.Equal(admin, Assert.Single(await context.MyAxisSessions.Select(s => s.UserId).ToListAsync()));
    }

    [Fact]
    public async Task ABackupHoldsNoMyAxisSession()
    {
        var sourceDir = TestDatabase.NewTempDirectory();
        var targetDir = TestDatabase.NewTempDirectory();
        try
        {
            var a = await TestDatabase.CreateAsync(sourceDir);
            var anna = await AddUserAsync(a, "anna");
            await StoreOf(a).SaveAsync(anna, FakeAxisCom.Session, CancellationToken.None);
            var file = new MemoryStream();
            await new BackupArchive(a.Paths) { Iterations = 1000 }.CreateAsync(file, "backup-password-1", new BackupHeader { Kind = "manual", OadmVersion = "4.9.0", ServerName = "a" }, [], CancellationToken.None);
            await a.CloseAsync();

            var b = await TestDatabase.CreateAsync(targetDir);
            await b.CloseAsync();
            var archive = new BackupArchive(b.Paths);
            file.Position = 0;
            await archive.StageAsync(file, "backup-password-1", "a.oadmbackup", "admin@PC", "10.0.0.5", CancellationToken.None);
            Assert.Null(archive.ApplyPending((_, _) => { })!.Error);

            var restored = await TestDatabase.CreateAsync(targetDir);
            await using (var context = await restored.Get<IDbContextFactory<OadmDbContext>>().CreateDbContextAsync())
            {
                Assert.True(await context.Users.AnyAsync(u => u.Id == anna));
                Assert.Equal(0, await context.MyAxisSessions.CountAsync());
            }

            Assert.Equal(MyAxisState.None, await StoreOf(restored).GetStateAsync(anna, CancellationToken.None));
            await restored.CloseAsync();
        }
        finally
        {
            TestDatabase.DeleteDirectory(sourceDir);
            TestDatabase.DeleteDirectory(targetDir);
        }
    }
}
