using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Ocr.Recognition;

namespace Oadm.Plugins.Ocr.Tests.Recognition;

/// <summary>
/// The graphics card's sessions (docs/spec/ocr.md "GPU", graphics card memory): at most a fixed number over all models of the
/// set, the least recently used goes with one log line, unused ones are freed after 2 minutes. Fake sessions, no graphics card.
/// </summary>
public sealed class GpuSessionBudgetTests
{
    private sealed class FakeSession(string name) : IDisposable
    {
        public string Name { get; } = name;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private static readonly object Detection = new();
    private static readonly object Reader = new();

    [Fact]
    public void The_cap_counts_every_model_and_frees_the_least_recently_used_with_one_log_line()
    {
        var capped = new List<int>();
        using var budget = new GpuSessionBudget(3, new FakeTimeProvider(), capped.Add);
        var made = new List<FakeSession>();
        FakeSession Get(object owner, string key) => budget.Get(owner, key, () =>
        {
            var session = new FakeSession(key);
            made.Add(session);
            return session;
        });

        var small = Get(Detection, "1x3x256x256");
        var large = Get(Detection, "6x3x1024x1024");
        var line = Get(Reader, "8x3x48x320");
        Assert.Same(small, Get(Detection, "1x3x256x256")); // used again: now the most recent
        Assert.Equal(3, made.Count);

        // A fourth size: the least recently used of any model goes (the large detection size), the reader's stays.
        Get(Reader, "32x3x48x640");
        Assert.Equal(3, budget.Count);
        Assert.True(large.Disposed);
        Assert.False(small.Disposed);
        Assert.False(line.Disposed);
        Assert.Equal(1, budget.Evicted);
        Assert.Equal([3], capped);

        // Asked for again, it is made anew; the log line stays the one.
        Get(Detection, "6x3x1024x1024");
        Assert.Equal(5, made.Count);
        Assert.Equal(2, budget.Evicted);
        Assert.Equal([3], capped);
    }

    [Fact]
    public void The_text_finder_keeps_at_most_its_own_share_and_frees_its_own_sessions_first()
    {
        using var budget = new GpuSessionBudget(6, new FakeTimeProvider());
        var line = budget.Get(Reader, "8x3x48x320", () => new FakeSession("line"));
        var sizes = Enumerable.Range(0, 3).Select(i => budget.Get(Detection, $"1x3x{256 * (i + 1)}x256", () => new FakeSession("d" + i), 3)).ToList();

        // A fourth size of the text finder: its own oldest goes, though the reader's session is older and the set has room.
        budget.Get(Detection, "6x3x768x768", () => new FakeSession("tiles"), 3);
        Assert.True(sizes[0].Disposed);
        Assert.False(line.Disposed);
        Assert.Equal(4, budget.Count);
    }

    [Fact]
    public void Sessions_unused_for_two_minutes_are_freed()
    {
        var time = new FakeTimeProvider();
        using var budget = new GpuSessionBudget(6, time);
        var old = budget.Get(Detection, "a", () => new FakeSession("a"));
        var recent = budget.Get(Reader, "b", () => new FakeSession("b"));

        time.Advance(TimeSpan.FromMinutes(1.5));
        budget.Get(Reader, "b", () => new FakeSession("never"));
        time.Advance(TimeSpan.FromMinutes(1)); // the timer runs every minute: at 2 minutes "a" idled 2, "b" half a minute

        Assert.True(old.Disposed);
        Assert.False(recent.Disposed);
        Assert.Equal(1, budget.Count);

        time.Advance(GpuSessionBudget.IdleTimeout);
        Assert.True(recent.Disposed);
        Assert.Equal(0, budget.Count);
    }

    [Fact]
    public void A_model_frees_only_its_own_sessions_and_dispose_frees_all()
    {
        var budget = new GpuSessionBudget(6, new FakeTimeProvider());
        var detection = budget.Get(Detection, "a", () => new FakeSession("a"));
        var reader = budget.Get(Reader, "a", () => new FakeSession("a")); // same size, another model: a session of its own
        Assert.NotSame(detection, reader);

        budget.Release(Detection);
        Assert.True(detection.Disposed);
        Assert.False(reader.Disposed);

        budget.Dispose();
        Assert.True(reader.Disposed);
        Assert.Throws<ObjectDisposedException>(() => budget.Get(Reader, "b", () => new FakeSession("b")));
    }
}
