using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

using Oadm.Core.Auth;
using Oadm.Core.MyAxis;
using Oadm.Core.Persistence;
using Oadm.Core.Security;
using Oadm.Core.Tests.Persistence;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Tests.Shared;

namespace Oadm.Core.Tests.MyAxis;

/// <summary>
/// The host's downloads from axis.com for tasks (docs/spec/myaxis.md "For plugins"): the URL allowlist, the session of the
/// user who started the task, expiry, one download per URL with joined progress, size and checksum, and the cache (reuse,
/// 7 days, 5 GB, checksum before use, files in use kept).
/// </summary>
public sealed class AxisDownloadServiceTests : IAsyncLifetime, IDisposable
{
    private static readonly Uri FileUri = new("https://www.axis.com/ftp/pub_soft/MPQT/AXIS_P3265/12_6_1/P3265_12_6_1.bin");
    private static readonly byte[] Content = [.. Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7))];
    private static readonly string Sha = Convert.ToHexStringLower(SHA256.HashData(Content));

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeAxisCom _axis = new();
    private readonly ConcurrentDictionary<string, int> _gets = new();
    private TestDatabase _db = null!;
    private MyAxisSessionStore _sessions = null!;
    private AxisDownloadService _service = null!;
    private Guid _anna;
    private Guid _ben;

    public async Task InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _sessions = new MyAxisSessionStore(_db.Get<IDbContextFactory<OadmDbContext>>(), _db.Get<CredentialProtector>(), _time);
        _anna = (await _db.Get<UserStore>().AddAsync("anna", "a-long-password-1", BuiltInRoles.OperatorId, CancellationToken.None)).Id;
        _ben = (await _db.Get<UserStore>().AddAsync("ben", "a-long-password-1", BuiltInRoles.OperatorId, CancellationToken.None)).Id;
        await _sessions.SaveAsync(_anna, FakeAxisCom.Session, CancellationToken.None);
        _axis.Respond = (request, _) => Task.FromResult(Serve(request, Content));
        _service = NewService();
        Directory.CreateDirectory(CacheDirectory);
    }

    public async Task DisposeAsync()
    {
        _service.Dispose();
        await _db.DisposeAsync();
    }

    public void Dispose() => _axis.Dispose();

    private string CacheDirectory => Path.Combine(_db.Paths.DataDirectory, "axis-downloads");

    private AxisDownloadService NewService(long maxBytes = AxisDownloadService.DefaultMaxBytes) =>
        new(CacheDirectory, _sessions, new AxisComClient(_axis, _time), _time) { MaxBytes = maxBytes };

    private HttpResponseMessage Serve(HttpRequestMessage request, byte[] body)
    {
        _gets.AddOrUpdate(request.RequestUri!.AbsoluteUri, 1, (_, n) => n + 1);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    }

    private int GetsOf(Uri uri) => _gets.GetValueOrDefault(uri.AbsoluteUri);

    private Task<IAxisDownloads> AnnaAsync() => _service.ForUserAsync(_anna, CancellationToken.None);

    private static Uri Other(int i) => new($"https://www.axis.com/ftp/pub_soft/PACS/AXIS_A{i}/latest/A{i}.bin");

    [Theory]
    [InlineData("https://www.axis.com/ftp/pub_soft/MPQT/../x.bin")]
    [InlineData("https://www.axis.com/en-us/x.bin")]
    [InlineData("https://downloads.example.com/ftp/pub_soft/MPQT/x.bin")]
    [InlineData("http://www.axis.com/ftp/pub_soft/MPQT/x.bin")]
    public async Task AUrlOutsideTheDownloadFoldersIsRefusedAndNothingIsSent(string url)
    {
        var downloads = await AnnaAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => downloads.DownloadAsync(new Uri(url), Sha, Content.Length, null, CancellationToken.None));
        Assert.Empty(_axis.Requests);
    }

    [Fact]
    public async Task WithoutASessionTheTaskIsAskedToSignIn()
    {
        var ben = await _service.ForUserAsync(_ben, CancellationToken.None);

        Assert.False(ben.HasSession);
        var error = await Assert.ThrowsAsync<AxisDownloadException>(() => ben.DownloadAsync(FileUri, Sha, Content.Length, null, CancellationToken.None));
        Assert.True(error.NeedsSignIn);
        Assert.Equal(AxisDownloadTexts.SignIn, error.Message);
        Assert.Empty(_axis.Requests);
    }

    [Fact]
    public async Task TheHandleHasTheRealLengthNotTheRoundedListedSize()
    {
        var anna = await AnnaAsync();
        var listed = Content.Length + 1000; // axis.com lists "225.5M"

        await using (var first = await anna.DownloadAsync(FileUri, Sha, listed, null, CancellationToken.None))
        {
            Assert.Equal(Content.Length, first.Length);
        }

        await using var second = await anna.DownloadAsync(FileUri, Sha, listed, null, CancellationToken.None);
        Assert.True(second.FromCache);
        Assert.Equal(Content.Length, second.Length);
    }

    [Fact]
    public async Task ADownloadUsesTheStartersSessionOnceAndTheCacheAfterwards()
    {
        var anna = await AnnaAsync();
        Assert.True(anna.HasSession);

        await using (var first = await anna.DownloadAsync(FileUri, Sha, Content.Length, null, CancellationToken.None))
        {
            Assert.False(first.FromCache);
            Assert.Equal(Content, await File.ReadAllBytesAsync(first.Path));
            Assert.StartsWith(CacheDirectory, first.Path, StringComparison.Ordinal);
            Assert.EndsWith("P3265_12_6_1.bin", first.Path, StringComparison.Ordinal);
        }

        _time.Advance(TimeSpan.FromDays(6));
        await using (var second = await anna.DownloadAsync(FileUri, Sha.ToUpperInvariant(), Content.Length, null, CancellationToken.None))
        {
            Assert.True(second.FromCache);
        }

        var request = Assert.Single(_axis.Requests);
        Assert.Equal((HttpMethod.Get, FileUri, FakeAxisCom.Session), (request.Method, request.Uri, request.Cookie));
    }

    [Fact]
    public async Task ACachedFileIsCheckedBeforeEveryUseAndAMismatchIsDownloadedAgain()
    {
        var anna = await AnnaAsync();
        string path;
        await using (var first = await anna.DownloadAsync(FileUri, Sha, Content.Length, null, CancellationToken.None))
        {
            path = first.Path;
        }

        var changed = (byte[])Content.Clone();
        changed[1000] ^= 0xff;
        await File.WriteAllBytesAsync(path, changed);

        await using (var again = await anna.DownloadAsync(FileUri, Sha, Content.Length, null, CancellationToken.None))
        {
            Assert.False(again.FromCache);
            Assert.Equal(Content, await File.ReadAllBytesAsync(again.Path));
        }

        Assert.Equal(2, GetsOf(FileUri));
    }

    [Fact]
    public async Task AFileIsKeptSevenDaysAfterItsLastUseButNeverWhileInUse()
    {
        var anna = await AnnaAsync();
        var held = await anna.DownloadAsync(FileUri, Sha, Content.Length, null, CancellationToken.None);

        _time.Advance(TimeSpan.FromDays(8));
        _service.Sweep();
        Assert.True(File.Exists(held.Path)); // in use

        await held.DisposeAsync();
        _service.Sweep();
        Assert.False(File.Exists(held.Path));

        await using (var used = await anna.DownloadAsync(FileUri, Sha, Content.Length, null, CancellationToken.None))
        {
            Assert.False(used.FromCache);
        }

        _time.Advance(TimeSpan.FromDays(6));
        await using (var reused = await anna.DownloadAsync(FileUri, Sha, Content.Length, null, CancellationToken.None))
        {
            Assert.True(reused.FromCache); // the use counts, not the download
        }

        _time.Advance(TimeSpan.FromDays(6.9));
        _service.Sweep();
        Assert.True(Directory.Exists(Path.Combine(CacheDirectory, Sha)));
        _time.Advance(TimeSpan.FromDays(0.2));
        _service.Sweep();
        Assert.False(Directory.Exists(Path.Combine(CacheDirectory, Sha)));
    }

    [Fact]
    public async Task ASweepChecksEachDeleteAgainSoAUseDuringTheWalkKeepsTheFile()
    {
        var anna = await AnnaAsync();
        await using (await anna.DownloadAsync(FileUri, Sha, Content.Length, null, CancellationToken.None))
        {
        }

        var folder = Path.Combine(CacheDirectory, Sha);
        var file = Directory.EnumerateFiles(folder).Single();
        _time.Advance(TimeSpan.FromDays(8));

        Action<string>? during = null;
        using var sweeper = new AxisDownloadService(CacheDirectory, _sessions, new AxisComClient(_axis, _time), _time)
        {
            BeforeSweepDelete = sha => during?.Invoke(sha),
        };

        // A task takes the file after the walk found it too old: it stays.
        during = sweeper.Acquire;
        sweeper.Sweep();
        Assert.True(File.Exists(file));
        sweeper.Release(Sha);

        // A task used it and let it go meanwhile: it stays too.
        during = _ => File.SetLastWriteTimeUtc(file, _time.GetUtcNow().UtcDateTime);
        sweeper.Sweep();
        Assert.True(File.Exists(file));

        during = null;
        _time.Advance(TimeSpan.FromDays(8));
        sweeper.Sweep();
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task TheCacheKeepsAtMostItsLimitTheOldestGoFirstNeverOneInUse()
    {
        Assert.Equal(5L * 1024 * 1024 * 1024, AxisDownloadService.DefaultMaxBytes);
        Assert.Equal(TimeSpan.FromDays(7), AxisDownloadService.DefaultKeepFor);
        _service.Dispose();
        _service = NewService(maxBytes: 2 * Content.Length); // the 5 GB rule at test size
        var anna = await AnnaAsync();
        var files = new List<(Uri Uri, byte[] Body, string Sha)>();
        for (var i = 0; i < 4; i++)
        {
            var body = Content.Select(b => (byte)(b + i)).ToArray();
            files.Add((Other(i), body, Convert.ToHexStringLower(SHA256.HashData(body))));
        }

        _axis.Respond = (request, _) => Task.FromResult(Serve(request, files.Single(f => f.Uri == request.RequestUri).Body));

        var oldestHeld = await anna.DownloadAsync(files[0].Uri, files[0].Sha, Content.Length, null, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(1));
        await (await anna.DownloadAsync(files[1].Uri, files[1].Sha, Content.Length, null, CancellationToken.None)).DisposeAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        await (await anna.DownloadAsync(files[2].Uri, files[2].Sha, Content.Length, null, CancellationToken.None)).DisposeAsync();

        // Three files over a limit of two: the oldest not in use went.
        Assert.True(Directory.Exists(Path.Combine(CacheDirectory, files[0].Sha)));
        Assert.False(Directory.Exists(Path.Combine(CacheDirectory, files[1].Sha)));
        Assert.True(Directory.Exists(Path.Combine(CacheDirectory, files[2].Sha)));

        await oldestHeld.DisposeAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        await (await anna.DownloadAsync(files[3].Uri, files[3].Sha, Content.Length, null, CancellationToken.None)).DisposeAsync();
        Assert.False(Directory.Exists(Path.Combine(CacheDirectory, files[0].Sha)));
        Assert.True(Directory.Exists(Path.Combine(CacheDirectory, files[2].Sha)));
        Assert.True(Directory.Exists(Path.Combine(CacheDirectory, files[3].Sha)));
    }

    [Fact]
    public async Task TasksThatNeedTheSameFileShareOneDownloadAndItsProgress()
    {
        await _sessions.SaveAsync(_ben, "axis_connect_session_sid=bens-session", CancellationToken.None);
        var body = new GatedStream(Content);
        _axis.Respond = (request, _) =>
        {
            _gets.AddOrUpdate(request.RequestUri!.AbsoluteUri, 1, (_, n) => n + 1);
            var content = new StreamContent(body);
            content.Headers.ContentLength = Content.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        };
        var annaSeen = new ConcurrentQueue<long>();
        var benSeen = new ConcurrentQueue<long>();
        var anna = await AnnaAsync();
        var ben = await _service.ForUserAsync(_ben, CancellationToken.None);

        var first = anna.DownloadAsync(FileUri, Sha, Content.Length, new SyncProgress(annaSeen.Enqueue), CancellationToken.None);
        await Eventually.TrueAsync(() => GetsOf(FileUri) == 1, "the download started");
        body.Release(100_000);
        await Eventually.TrueAsync(() => annaSeen.Contains(100_000), "the first task saw the first bytes");

        var second = ben.DownloadAsync(FileUri, Sha, Content.Length, new SyncProgress(benSeen.Enqueue), CancellationToken.None);
        await Eventually.TrueAsync(() => benSeen.Contains(100_000), "the second task joined and saw the bytes so far");
        body.ReleaseAll();

        await using var a = await first;
        await using var b = await second;
        Assert.Equal(a.Path, b.Path);
        Assert.Equal(1, GetsOf(FileUri));
        Assert.Equal(Content.Length, annaSeen.Max());
        Assert.Equal(Content.Length, benSeen.Max());
        Assert.All(_axis.Requests, r => Assert.Equal(FakeAxisCom.Session, r.Cookie)); // the starter's session only
    }

    [Fact]
    public async Task AnExpiredSessionIsDeletedAndTheTaskAskedToSignInAgain()
    {
        foreach (var answer in new Func<HttpResponseMessage>[]
        {
            () => new HttpResponseMessage(HttpStatusCode.Forbidden),
            () => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            () => FakeAxisCom.RedirectTo("https://www.axis.com/my-axis/login?axis_destination=x"),
        })
        {
            await _sessions.SaveAsync(_anna, FakeAxisCom.Session, CancellationToken.None);
            _axis.Respond = (_, _) => Task.FromResult(answer());
            var anna = await AnnaAsync();

            var error = await Assert.ThrowsAsync<AxisDownloadException>(() => anna.DownloadAsync(FileUri, Sha, Content.Length, null, CancellationToken.None));

            Assert.True(error.NeedsSignIn);
            Assert.Equal(AxisDownloadTexts.Expired, error.Message);
            Assert.False(anna.HasSession);
            Assert.Equal(new MyAxisState(false, null, true), await _sessions.GetStateAsync(_anna, CancellationToken.None));
        }

        Assert.Empty(Directory.EnumerateFiles(CacheDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ARedirectElsewhereIsNotFollowedAndKeepsTheSession()
    {
        _axis.Respond = (_, _) => Task.FromResult(FakeAxisCom.RedirectTo("https://cdn.example.com/x.bin"));
        var anna = await AnnaAsync();

        var error = await Assert.ThrowsAsync<AxisDownloadException>(() => anna.DownloadAsync(FileUri, Sha, Content.Length, null, CancellationToken.None));

        Assert.False(error.NeedsSignIn);
        Assert.Equal(AxisDownloadTexts.NotDownloaded("axis.com answered 302"), error.Message);
        Assert.Single(_axis.Requests);
        Assert.True((await _sessions.GetStateAsync(_anna, CancellationToken.None)).SignedIn);
    }

    [Fact]
    public async Task AWrongSizeOrChecksumKeepsNothing()
    {
        var anna = await AnnaAsync();
        var bigger = Content.Concat(new byte[10]).ToArray();
        var cases = new (Func<HttpResponseMessage> Answer, string What)[]
        {
            (() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bigger) }, "announced larger"),
            (() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(Content[..^10])) }, "shorter"),
            (() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new UnknownLength(new byte[Content.Length * 2])) }, "far larger, no length"),
            (() =>
            {
                var changed = (byte[])Content.Clone();
                changed[5] ^= 1;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(changed) };
            }, "another SHA-256"),
        };

        foreach (var (answer, what) in cases)
        {
            _axis.Respond = (_, _) => Task.FromResult(answer());

            var error = await Assert.ThrowsAsync<AxisDownloadException>(() => anna.DownloadAsync(FileUri, Sha, Content.Length, null, CancellationToken.None));

            Assert.True(error.Message == AxisDownloadTexts.Mismatch, what);
            Assert.Empty(Directory.EnumerateFiles(CacheDirectory, "*", SearchOption.AllDirectories));
        }

        Assert.True((await _sessions.GetStateAsync(_anna, CancellationToken.None)).SignedIn);
    }

    [Fact]
    public async Task ADownloadThatGrowsPastTheLimitStopsThere()
    {
        var read = new UnknownLength(new byte[Content.Length + (Content.Length / 50) + (2 * 1024 * 1024)]);
        _axis.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(read) });
        var anna = await AnnaAsync();

        await Assert.ThrowsAsync<AxisDownloadException>(() => anna.DownloadAsync(FileUri, Sha, Content.Length, null, CancellationToken.None));

        Assert.True(read.BytesRead <= Content.Length + (Content.Length / 50) + (1024 * 1024) + 81920 + 65536, $"read {read.BytesRead} bytes");
    }

    [Fact]
    public async Task NoConnectionSaysSoInShort()
    {
        _axis.Respond = (_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused");
        var anna = await AnnaAsync();

        var error = await Assert.ThrowsAsync<AxisDownloadException>(() => anna.DownloadAsync(FileUri, Sha, Content.Length, null, CancellationToken.None));

        Assert.Equal("AXIS OS could not be downloaded from axis.com (no connection to axis.com). Nothing was changed.", error.Message);
        Assert.False(error.NeedsSignIn);
        Assert.True(anna.HasSession);
    }

    [Fact]
    public async Task TheStartSweepRemovesPartialDownloadsAndFilesTooOld()
    {
        var anna = await AnnaAsync();
        await (await anna.DownloadAsync(FileUri, Sha, Content.Length, null, CancellationToken.None)).DisposeAsync();
        Directory.CreateDirectory(Path.Combine(CacheDirectory, "tmp"));
        var partial = Path.Combine(CacheDirectory, "tmp", "left-over.part");
        await File.WriteAllBytesAsync(partial, [1, 2, 3]);

        var restarted = NewService();
        restarted.Sweep();
        Assert.False(File.Exists(partial));
        Assert.True(Directory.Exists(Path.Combine(CacheDirectory, Sha)));

        _time.Advance(TimeSpan.FromDays(7));
        restarted.Sweep();
        Assert.False(Directory.Exists(Path.Combine(CacheDirectory, Sha)));
        restarted.Dispose();
    }

    [Fact]
    public async Task TheFakeOfTheSdkRefusesLikeTheHost()
    {
        using var fake = new FakeAxisDownloads();
        var sha = fake.Add(FileUri, Content);

        await using (var file = await fake.DownloadAsync(FileUri, sha, Content.Length, null, CancellationToken.None))
        {
            Assert.Equal(Content, await File.ReadAllBytesAsync(file.Path));
            Assert.Equal(1, fake.OpenHandles);
        }

        Assert.Equal(0, fake.OpenHandles);
        await Assert.ThrowsAsync<ArgumentException>(() => fake.DownloadAsync(new Uri("https://example.com/ftp/pub_soft/MPQT/a.bin"), sha, 1, null, CancellationToken.None));
        Assert.Equal(AxisDownloadTexts.Mismatch, (await Assert.ThrowsAsync<AxisDownloadException>(() => fake.DownloadAsync(FileUri, sha, Content.Length + 1_000_000, null, CancellationToken.None))).Message);
        fake.Expire();
        Assert.Equal(AxisDownloadTexts.Expired, (await Assert.ThrowsAsync<AxisDownloadException>(() => fake.DownloadAsync(FileUri, sha, Content.Length, null, CancellationToken.None))).Message);
        Assert.False(fake.HasSession);
        Assert.True((await Assert.ThrowsAsync<AxisDownloadException>(() => fake.DownloadAsync(FileUri, sha, Content.Length, null, CancellationToken.None))).NeedsSignIn);

        var context = new FakeTaskContext(new FakeCamera()) { AxisDownloads = fake };
        Assert.Same(fake, ((ITaskExecutionContext)context).AxisDownloads);
    }

    /// <summary>Progress reported on the calling thread (no synchronization context).</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    /// <summary>A body without a known length; <see cref="BytesRead"/> counts what was read.</summary>
    private sealed class UnknownLength(byte[] content) : MemoryStream(content)
    {
        private long _read;

        public override bool CanSeek => false;

        public long BytesRead => Interlocked.Read(ref _read);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            Interlocked.Add(ref _read, count);
            return count;
        }
    }
}
