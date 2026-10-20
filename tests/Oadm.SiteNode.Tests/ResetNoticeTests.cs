using Microsoft.Extensions.Time.Testing;

using Oadm.SiteNode.Feed;

namespace Oadm.SiteNode.Tests;

/// <summary>Reset tells the server first, but never waits for a server that does not take the notice.</summary>
public sealed class ResetNoticeTests
{
    [Fact]
    public async Task WithoutAWatchNothingIsAnnounced()
    {
        var notice = new ResetNotice(new FakeTimeProvider(), ResetNotice.DefaultWaitLimit);
        var asked = 0;
        notice.Requested += () => asked++;

        Assert.False(await notice.AnnounceAsync(CancellationToken.None));
        Assert.Equal(0, asked);
        Assert.False(notice.Pending);
    }

    [Fact]
    public async Task TheAnnouncementEndsWhenTheServerEndsEveryWatch()
    {
        var notice = new ResetNotice(new FakeTimeProvider(), ResetNotice.DefaultWaitLimit);
        var first = notice.Enter();
        var second = notice.Enter();

        var announce = notice.AnnounceAsync(CancellationToken.None);
        Assert.True(notice.Pending);
        first.Dispose();
        first.Dispose(); // once only
        Assert.False(announce.IsCompleted);
        second.Dispose();

        Assert.True(await announce);
        Assert.False(notice.Pending);
    }

    [Fact]
    public async Task AServerThatDoesNotTakeTheNoticeIsWaitedForAtMostTheLimit()
    {
        var time = new FakeTimeProvider();
        var notice = new ResetNotice(time, ResetNotice.DefaultWaitLimit);
        using var watch = notice.Enter();

        var announce = notice.AnnounceAsync(CancellationToken.None);
        time.Advance(ResetNotice.DefaultWaitLimit - TimeSpan.FromMilliseconds(1));
        Assert.False(announce.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.False(await announce);
        Assert.False(notice.Pending);
    }
}
