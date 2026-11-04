using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Ocr.Recognition;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// The graphics card's memory (docs/spec/ocr.md "GPU", graphics card memory): sessions counted by their input pixels, the sum
/// never above the cap, the least recently used goes first with one log line, a session larger than the cap is never made,
/// unused ones are freed after 2 minutes, and a Search is split into calls that fit. Fake sessions, no graphics card.
/// </summary>
public sealed class GpuSessionBudgetTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private sealed class FakeSession(string name) : IDisposable
    {
        public string Name { get; } = name;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private static readonly object Detection = new();
    private static readonly object Other = new();

    [Fact]
    public void The_cap_is_80_percent_of_the_card_and_4_GB_when_the_card_does_not_tell()
    {
        var card = 16L * 1024 * 1024 * 1024;
        Assert.Equal((long)(card * 0.8), GpuSessionBudget.CapFor(card));
        Assert.Equal(GpuSessionBudget.MaxBytes, GpuSessionBudget.CapFor(null));
        Assert.Equal(GpuSessionBudget.MaxBytes, GpuSessionBudget.CapFor(0));
    }

    [Theory]
    [InlineData("15997 MB", 15997L * 1024 * 1024)]
    [InlineData("0 MB", null)]
    [InlineData("16 GB", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_cards_memory_is_read_from_what_DirectML_tells(string? text, long? bytes) =>
        Assert.Equal(bytes, SessionFactory.ParseVideoMemory(text));

    [Fact]
    public void The_estimate_follows_the_measured_memory_of_the_text_finder()
    {
        // Measured on DirectML (spec "GPU"), GB of the card: the estimate is never below it and not far above.
        Assert.InRange(GpuSessionBudget.Estimate(256 * 256) / (double)Gb, 0.3, 0.7);
        Assert.InRange(GpuSessionBudget.Estimate(768 * 768) / (double)Gb, 1.3, 1.5);
        Assert.InRange(GpuSessionBudget.Estimate(1024 * 1024) / (double)Gb, 1.7, 2.0);
        Assert.InRange(GpuSessionBudget.Estimate(2 * 768 * 768) / (double)Gb, 2.14, 2.4);
        Assert.InRange(GpuSessionBudget.Estimate(6 * 768 * 768) / (double)Gb, 5.0, 5.5);
        Assert.InRange(GpuSessionBudget.Estimate(6 * 1024 * 1024) / (double)Gb, 8.6, 9.5);
    }

    [Fact]
    public void Sessions_are_counted_by_their_pixels_and_the_least_recently_used_go_until_a_new_one_fits()
    {
        var capped = new List<long>();
        using var budget = new GpuSessionBudget(4 * Gb, new FakeTimeProvider(), capped.Add);
        var made = new List<FakeSession>();
        FakeSession Get(object owner, string key, long bytes) => budget.Get(owner, key, bytes, () =>
        {
            var session = new FakeSession(key);
            made.Add(session);
            return session;
        });

        var a = Get(Detection, "a", 1 * Gb);
        var b = Get(Detection, "b", 1 * Gb);
        var c = Get(Other, "c", 1 * Gb);
        Assert.Same(a, Get(Detection, "a", 1 * Gb)); // used again: now the most recent
        Assert.Equal(3 * Gb, budget.Bytes);

        // 2 GB more: the least recently used of any model goes ("b"), then "c": "a" stays.
        Get(Detection, "large", 2 * Gb);
        Assert.True(b.Disposed);
        Assert.False(c.Disposed);
        Assert.False(a.Disposed);
        Assert.Equal(4 * Gb, budget.Bytes);
        Assert.Equal(1, budget.Evicted);
        Assert.Equal([4 * Gb], capped);

        Get(Detection, "d", 1 * Gb);
        Assert.True(c.Disposed);
        Assert.Equal(2, budget.Evicted);
        Assert.Equal([4 * Gb], capped); // the log line stays the one
        Assert.True(budget.Bytes <= budget.Max);
    }

    [Fact]
    public void A_session_larger_than_the_cap_is_never_made()
    {
        using var budget = new GpuSessionBudget(4 * Gb, new FakeTimeProvider());
        var kept = budget.Get(Detection, "a", 1 * Gb, () => new FakeSession("a"));
        var made = false;

        Assert.Throws<ArgumentOutOfRangeException>(() => budget.Get(Detection, "huge", 5 * Gb, () =>
        {
            made = true;
            return new FakeSession("huge");
        }));
        Assert.False(made);
        Assert.False(kept.Disposed);
        Assert.Equal(1, budget.Count);
    }

    [Theory]
    [InlineData(6, 768, 768, 1)] // full HD Search: 6 calls of 1 tile (2 tiles: about 2.2 GB)
    [InlineData(6, 1024, 1024, 1)] // QHD
    [InlineData(15, 1024, 1024, 1)] // 4K
    [InlineData(4, 512, 512, 4)] // a smaller area: one call
    [InlineData(5, 512, 512, 3)] // 2 calls of 3, the last with one blank tile
    [InlineData(1, 1024, 1024, 1)]
    public void Tiles_per_call_keep_each_call_within_half_the_cap(int tiles, int width, int height, int expected)
    {
        var perCall = GpuSessionBudget.TilesPerCall(tiles, width, height);

        Assert.Equal(expected, perCall);
        Assert.True(GpuSessionBudget.Estimate((long)perCall * width * height) <= GpuSessionBudget.MaxCallBytes);
    }

    [Fact]
    public void Tiles_are_spread_evenly_over_the_fewest_calls()
    {
        // 7 tiles of which 4 fit a call: 2 calls of 4 (one blank), not 4 + 3 (two sessions).
        var pixelsOfFour = (GpuSessionBudget.MaxCallBytes - GpuSessionBudget.SessionBytes) / GpuSessionBudget.BytesPerPixel / 4;
        var side = (int)Math.Sqrt(pixelsOfFour);
        Assert.Equal(4, GpuSessionBudget.TilesPerCall(7, side, side));
        Assert.Equal(3, GpuSessionBudget.TilesPerCall(6, side, side));
    }

    [Fact]
    public void Sessions_unused_for_two_minutes_are_freed()
    {
        var time = new FakeTimeProvider();
        using var budget = new GpuSessionBudget(4 * Gb, time);
        var old = budget.Get(Detection, "a", Gb, () => new FakeSession("a"));
        var recent = budget.Get(Other, "b", Gb, () => new FakeSession("b"));

        time.Advance(TimeSpan.FromMinutes(1.5));
        budget.Get(Other, "b", Gb, () => new FakeSession("never"));
        time.Advance(TimeSpan.FromMinutes(1)); // the timer runs every minute: at 2 minutes "a" idled 2, "b" half a minute

        Assert.True(old.Disposed);
        Assert.False(recent.Disposed);
        Assert.Equal(1, budget.Count);
        Assert.Equal(Gb, budget.Bytes);

        time.Advance(GpuSessionBudget.IdleTimeout);
        Assert.True(recent.Disposed);
        Assert.Equal(0, budget.Count);
        Assert.Equal(0, budget.Bytes);
    }

    [Fact]
    public void A_model_frees_only_its_own_sessions_and_dispose_frees_all()
    {
        var budget = new GpuSessionBudget(4 * Gb, new FakeTimeProvider());
        var detection = budget.Get(Detection, "a", Gb, () => new FakeSession("a"));
        var other = budget.Get(Other, "a", Gb, () => new FakeSession("a")); // same size, another model: a session of its own
        Assert.NotSame(detection, other);

        budget.Release(Detection);
        Assert.True(detection.Disposed);
        Assert.False(other.Disposed);
        Assert.Equal(Gb, budget.Bytes);

        budget.Dispose();
        Assert.True(other.Disposed);
        Assert.Throws<ObjectDisposedException>(() => budget.Get(Other, "b", Gb, () => new FakeSession("b")));
    }
}
