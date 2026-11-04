using System.Text;

using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;
using Oadm.Plugins.Ocr.Readers;

namespace Oadm.Plugins.Ocr.Tests.Readers;

/// <summary>One area of a watching reader: the vote with the frames its values were read in, and Moving objects (docs/spec/ocr.md "Moving objects").</summary>
public sealed class AreaWatchTests
{
    private static readonly RelativeBox Box = new(0.1, 0.2, 0.3, 0.1);

    private static AreaResult Result(string text) => new() { Area = "Parcel", Text = text, TextRead = text, Confidence = text.Length == 0 ? 0 : 90 };

    /// <summary>A frame of its own for each value: the array tells which frame a value came from.</summary>
    private static ValueFrame Seen(string text, double x = 0.1) => new(Encoding.UTF8.GetBytes(text), Box with { X = x }, new LineTurn(10, 20, 30, 4, 1));

    private static List<AreaDecision> Feed(AreaWatch watch, params string[] texts) => [.. texts.Select(t => watch.Add(Result(t), Seen(t)))];

    private static string[] Published(List<AreaDecision> decisions) => [.. decisions.Where(d => d.Publish).Select(d => d.Voted.Text)];

    [Fact]
    public void Off_gives_the_same_values_as_the_plain_vote()
    {
        string[] frames = ["AAAA", "AAAA", "BBBB", "AAAA", "CCCC", "CCCC", "CCCC", string.Empty, string.Empty, string.Empty, "DDDD", "DDDD"];
        var vote = new FrameVote();
        var plain = frames.Select(t => vote.Add(new TextMatch { Text = t, Confidence = t.Length == 0 ? 0 : 90 })?.Text).ToList();

        var decisions = Feed(new AreaWatch(), frames);

        Assert.Equal(plain, decisions.Select(d => d.Publish ? d.Voted.Text : null));
    }

    [Fact]
    public void A_voted_value_this_frame_does_not_hold_comes_from_its_own_frame()
    {
        var watch = new AreaWatch();
        var (first, second, newest) = (Seen("AAAA", 0.1), Seen("BBBB", 0.2), Seen("CCCC", 0.3));

        watch.Add(Result("AAAA"), first);
        watch.Add(Result("BBBB"), second);
        var own = Seen("AAAA", 0.4);
        watch.Add(Result("AAAA"), own);
        var late = watch.Add(Result("CCCC"), newest);

        Assert.True(late.Publish);
        Assert.Equal("AAAA", late.Voted.Text);
        Assert.Equal(own.Jpeg, late.Seen.Jpeg);
        Assert.Equal(own.Box, late.Seen.Box);
        Assert.Equal(own.Turn, late.Seen.Turn);
    }

    [Fact]
    public void A_value_the_vote_put_together_comes_from_the_newest_frame_of_its_length()
    {
        var watch = new AreaWatch();
        watch.Add(Result("ABX"), Seen("ABX", 0.1));
        watch.Add(Result("AXC"), Seen("AXC", 0.2));
        var last = Seen("XBC", 0.3);

        var decision = watch.Add(Result("XBC"), last);

        Assert.Equal("ABC", decision.Voted.Text);
        Assert.Equal(last.Jpeg, decision.Seen.Jpeg);
        Assert.Equal(last.Box, decision.Seen.Box);
    }

    [Fact]
    public void The_vote_holds_at_most_its_five_frames_and_none_for_an_empty_value()
    {
        var watch = new AreaWatch();
        for (var i = 0; i < 100; i++)
        {
            watch.Add(Result("AAAA"), Seen("AAAA"));
            Assert.InRange(watch.HeldFrames, 1, FrameVote.Window);
        }

        Assert.Equal(FrameVote.Window, watch.HeldFrames);
        Feed(watch, string.Empty, string.Empty, string.Empty);
        Assert.Equal(2, watch.HeldFrames);
        Feed(watch, string.Empty, string.Empty);
        Assert.Equal(0, watch.HeldFrames);
    }

    [Fact]
    public void Moving_objects_need_two_frames_in_a_row()
    {
        var watch = new AreaWatch(ExpectedText.AnyText, moving: true);

        var first = watch.Add(Result("AAAA"), Seen("AAAA"));
        Assert.False(first.Publish);
        Assert.Empty(first.Voted.Text); // nothing stands yet
        var kept = Seen("AAAA");
        var second = watch.Add(Result("AAAA"), kept);

        Assert.True(second.Publish);
        Assert.Equal("AAAA", second.Voted.Text);
        Assert.Equal(kept.Jpeg, second.Seen.Jpeg); // the second frame's picture
        Assert.Equal(kept.Box, second.Seen.Box);
        Assert.Equal(kept.Turn, second.Seen.Turn);
    }

    [Fact]
    public void A_value_seen_once_changes_nothing() =>
        Assert.Equal(["AAAA"], Published(Feed(new AreaWatch(ExpectedText.AnyText, moving: true), "AAAA", "AAAA", "XXXX", "AAAA", "AAAA", "YYYY", "ZZZZ", "AAAA")));

    [Fact]
    public void A_value_that_stays_is_one_and_a_b_a_gives_three() =>
        Assert.Equal(["AAAA", "BBBB", "AAAA"], Published(Feed(new AreaWatch(ExpectedText.AnyText, moving: true), "AAAA", "AAAA", "AAAA", "AAAA", "BBBB", "BBBB", "AAAA", "AAAA", "AAAA")));

    [Fact]
    public void An_area_that_becomes_empty_publishes_once_and_a_never_read_one_never() =>
        Assert.Equal(["AAAA", string.Empty, "BBBB"], Published(Feed(new AreaWatch(ExpectedText.AnyText, moving: true), string.Empty, string.Empty, "AAAA", "AAAA", string.Empty, string.Empty, string.Empty, "BBBB", "BBBB")));

    [Fact]
    public void Between_values_the_area_shows_the_last_one_without_this_frames_box()
    {
        var watch = new AreaWatch(ExpectedText.AnyText, moving: true);
        Feed(watch, "AAAA", "AAAA");

        var misread = watch.Add(Result("XXXX"), Seen("XXXX"));

        Assert.False(misread.Publish);
        Assert.Equal("AAAA", misread.Voted.Text);
        Assert.Equal("XXXX", misread.Voted.TextRead); // the raw text stays this frame's
    }

    [Fact]
    public void Moving_objects_hold_only_the_frame_of_their_value()
    {
        var watch = new AreaWatch(ExpectedText.AnyText, moving: true);
        for (var i = 0; i < 100; i++)
        {
            Feed(watch, "A" + i, "A" + i, "X" + i);
            Assert.Equal(1, watch.HeldFrames);
        }

        Feed(watch, string.Empty, string.Empty);
        Assert.Equal(0, watch.HeldFrames);
    }
}
