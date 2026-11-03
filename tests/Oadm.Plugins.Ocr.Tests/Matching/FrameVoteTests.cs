using Oadm.Plugins.Ocr.Matching;
using Oadm.Plugins.Ocr.Model;

namespace Oadm.Plugins.Ocr.Tests.Matching;

/// <summary>The vote of a watching reader's area over its last frames (docs/spec/ocr.md "Accuracy", "Events and the rule action").</summary>
public sealed class FrameVoteTests
{
    private static TextMatch Read(string text, double confidence = 90) =>
        text.Length == 0 ? TextMatch.Empty : new TextMatch { Text = text, TextRead = text, Confidence = confidence, MatchesFormat = true };

    /// <summary>Feeds the readings and returns what each published (null: nothing).</summary>
    private static List<string?> Feed(FrameVote vote, params TextMatch[] readings) =>
        [.. readings.Select(r => vote.Add(r)?.Text)];

    [Fact]
    public void AValueIsPublishedOnceItHoldsInTwoFramesInARow()
    {
        var vote = new FrameVote();

        Assert.Equal([null, "MSCU 123456 6", null, null], Feed(vote, Read("MSCU 123456 6"), Read("MSCU 123456 6"), Read("MSCU 123456 6"), Read("MSCU 123456 6")));
        Assert.Equal("MSCU 123456 6", vote.Published);
    }

    [Fact]
    public void ASingleBadFrameChangesNothing()
    {
        var vote = new FrameVote();
        Feed(vote, Read("12345"), Read("12345"), Read("12345"));

        // Motion blur: one wrong character, then a hand in front (nothing read), then a wrong length.
        Assert.Equal([null, null, null, null], Feed(vote, Read("12845", 60), Read(""), Read("1234", 70), Read("12345")));
        Assert.Equal("12345", vote.Vote);
    }

    [Fact]
    public void CharactersAreVotedByConfidence()
    {
        var vote = new FrameVote();

        // Two frames read the third character as 8 with little confidence, one as 3 with much.
        vote.Add(Read("12845", 30));
        vote.Add(Read("12845", 30));
        var published = vote.Add(Read("12345", 99));

        Assert.Equal("12345", vote.Vote);
        Assert.Null(published);
        Assert.Equal("12345", vote.Add(Read("12845", 30))?.Text);
    }

    [Fact]
    public void ACombinedValueTakesTheFieldsOfAReadingOfItsLength()
    {
        var vote = new FrameVote();

        // No single frame read "12345": the vote combines them character by character.
        vote.Add(Read("12395", 80));
        vote.Add(Read("18345", 80));
        vote.Add(Read("12340", 80));
        var published = vote.Add(Read("12355", 50));

        Assert.NotNull(published);
        Assert.Equal("12345", published.Text);
        Assert.Equal(12345, published.Number);
    }

    [Fact]
    public void OnlyAChangeIsPublished()
    {
        var vote = new FrameVote();

        var published = Feed(vote, Read("A1"), Read("A1"), Read("B2"), Read("B2"), Read("B2"), Read("B2"), Read("A1"), Read("A1"), Read("A1"), Read("A1"));

        Assert.Equal([null, "A1", null, null, "B2", null, null, null, null, "A1"], published);
    }

    [Fact]
    public void AnAreaThatBecomesEmptyPublishesOnce()
    {
        var vote = new FrameVote();
        Feed(vote, Read("4711"), Read("4711"));

        var published = Feed(vote, Read(""), Read(""), Read(""), Read(""), Read(""), Read(""));

        Assert.Equal([null, null, string.Empty, null, null, null], published);
        Assert.Equal(string.Empty, vote.Published);
    }

    [Fact]
    public void AnAreaNeverReadPublishesNothingWhileEmpty()
    {
        var vote = new FrameVote();

        Assert.All(Feed(vote, Read(""), Read(""), Read(""), Read(""), Read(""), Read("")), Assert.Null);
    }

    [Fact]
    public void TheVoteForgetsFramesOlderThanTheWindow()
    {
        var vote = new FrameVote();
        Feed(vote, Read("AAA", 99), Read("AAA", 99), Read("AAA", 99), Read("AAA", 99), Read("AAA", 99));

        var published = Feed(vote, Read("BBB", 10), Read("BBB", 10), Read("BBB", 10), Read("BBB", 10), Read("BBB", 10), Read("BBB", 10));

        // With 5 frames in the window, "BBB" outweighs "AAA" only once all five old frames are gone, then holds a second frame.
        Assert.Equal(5, FrameVote.Window);
        Assert.Equal([null, null, null, null, null, "BBB"], published);
    }

    [Fact]
    public void ResetForgetsEverything()
    {
        var vote = new FrameVote();
        Feed(vote, Read("1"), Read("1"));

        vote.Reset();

        Assert.Equal(string.Empty, vote.Published);
        Assert.Equal([null, "1"], Feed(vote, Read("1"), Read("1")));
    }
}
