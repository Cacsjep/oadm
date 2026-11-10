using Oadm.Plugins.Vision.Model;
using Oadm.Plugins.Vision.Tests.Nodes;
using Oadm.Plugins.Vision.Tracking;

namespace Oadm.Plugins.Vision.Tests.Tracking;

public sealed class ObjectTrackerTests
{
    private static readonly DateTimeOffset T0 = TrackFixture.Start;

    /// <summary>Runs a fixture and gives, per person of the recording, the ids the tracker gave it, and the objects per frame.</summary>
    private static (Dictionary<int, HashSet<int>> IdsOf, List<int> TrackedPerFrame) Run(TrackFixture fixture, TrackerSettings? settings = null)
    {
        var tracker = new ObjectTracker(settings ?? TrackerSettings.Default);
        var idsOf = new Dictionary<int, HashSet<int>>();
        var perFrame = new List<int>();
        foreach (var frame in fixture.Frames)
        {
            var input = frame.Objects;
            var output = tracker.Update(input, frame.At);
            perFrame.Add(output.Count);
            foreach (var o in output)
            {
                var index = input.ToList().FindIndex(i => ReferenceEquals(i.Detection, o.Detection));
                var truth = frame.Detections[index].Truth;
                if (!idsOf.TryGetValue(truth, out var set))
                {
                    idsOf[truth] = set = [];
                }

                set.Add(o.TrackId!.Value);
            }
        }

        return (idsOf, perFrame);
    }

    [Fact]
    public void Two_people_passing_each_other_keep_their_ids()
    {
        var (idsOf, _) = Run(TrackFixture.Load("two-people-crossing.json"));
        Assert.Equal([1], idsOf[1]);
        Assert.Equal([2], idsOf[2]);
    }

    [Fact]
    public void An_id_stays_through_a_short_occlusion()
    {
        var (idsOf, perFrame) = Run(TrackFixture.Load("short-occlusion.json"));
        Assert.Equal([1], idsOf[1]);
        Assert.Contains(0, perFrame); // the hidden frames give nothing
    }

    [Fact]
    public void An_occlusion_longer_than_keep_lost_gives_a_new_id()
    {
        var (idsOf, _) = Run(TrackFixture.Load("short-occlusion.json"), new TrackerSettings(3, TimeSpan.FromSeconds(0.2)));
        Assert.Equal([1, 2], idsOf[1].Order());
    }

    [Fact]
    public void A_low_score_frame_keeps_the_object_through_the_second_round()
    {
        var fixture = TrackFixture.Load("back-and-forth.json");
        var (idsOf, perFrame) = Run(fixture);
        Assert.Equal([1], idsOf[1]);
        Assert.True(fixture.Frames[30].Detections[0].Detection.Score < ObjectTracker.HighScore);
        Assert.Equal(1, perFrame[30]);
    }

    [Fact]
    public void Objects_whose_scores_stay_low_are_tracked_too()
    {
        // Oranges on a belt at 20 to 30 %: no detection ever reaches the high score, each moves a little per frame.
        var fixture = TrackFixture.Load("two-people-crossing.json");
        var low = fixture.Frames.Select(f => f with
        {
            Detections = [.. f.Detections.Select(d => (d.Truth, d.Detection with { Score = 0.25 }))],
        }).ToList();
        Assert.All(low.SelectMany(f => f.Objects), o => Assert.True(o.Detection.Score < ObjectTracker.HighScore));
        var (idsOf, perFrame) = Run(fixture with { Frames = low });
        Assert.Equal([1], idsOf[1]);
        Assert.Equal([2], idsOf[2]);
        Assert.Equal(0, perFrame[1]); // still not confirmed in the second frame (Confirm after 3)
    }

    [Fact]
    public void A_crowd_of_fifty_keeps_one_id_per_person()
    {
        var fixture = TrackFixture.Load("crowd-50.json");
        var (idsOf, perFrame) = Run(fixture);
        Assert.Equal(55, idsOf.Count);
        Assert.All(idsOf, p => Assert.Single(p.Value));
        Assert.Equal(55, idsOf.Values.SelectMany(s => s).Distinct().Count());

        // Once confirmed, almost every detection is tracked (a missed detection is a lost track, not an object).
        for (var i = 5; i < perFrame.Count; i++)
        {
            Assert.True(perFrame[i] >= fixture.Frames[i].Detections.Count - 3, $"frame {i}: {perFrame[i]} of {fixture.Frames[i].Detections.Count}");
        }
    }

    [Fact]
    public void An_object_gets_its_id_after_the_confirm_frames()
    {
        var tracker = new ObjectTracker(new TrackerSettings(3, TimeSpan.FromSeconds(1)));
        Assert.Empty(tracker.Update([Obj(Det.Person(0.5, 0.8))], T0));
        Assert.Empty(tracker.Update([Obj(Det.Person(0.51, 0.8))], T0.AddSeconds(0.125)));
        var third = tracker.Update([Obj(Det.Person(0.52, 0.8))], T0.AddSeconds(0.25));
        Assert.Equal(1, Assert.Single(third).TrackId);
        Assert.Equal(3, third[0].Trail.Count);
    }

    [Fact]
    public void A_tentative_object_missed_once_starts_again()
    {
        var tracker = new ObjectTracker(new TrackerSettings(2, TimeSpan.FromSeconds(1)));
        tracker.Update([Obj(Det.Person(0.5, 0.8))], T0);
        tracker.Update([], T0.AddSeconds(0.125));
        Assert.Empty(tracker.Update([Obj(Det.Person(0.5, 0.8))], T0.AddSeconds(0.25)));
        Assert.Equal(1, Assert.Single(tracker.Update([Obj(Det.Person(0.5, 0.8))], T0.AddSeconds(0.375))).TrackId);
    }

    [Fact]
    public void Classes_are_matched_apart()
    {
        var tracker = new ObjectTracker(new TrackerSettings(1, TimeSpan.FromSeconds(1)));
        var person = Det.Person(0.5, 0.8, w: 0.2, h: 0.15);
        var car = Det.Car(0.5, 0.8);
        var first = tracker.Update([Obj(person), Obj(car)], T0);
        Assert.Equal([1, 2], first.Select(o => o.TrackId!.Value));

        // The person's box now sits where the car was: still its own id.
        var second = tracker.Update([Obj(Det.Car(0.5, 0.8)), Obj(Det.Person(0.5, 0.8, w: 0.2, h: 0.15))], T0.AddSeconds(0.125));
        Assert.Equal([2, 1], second.Select(o => o.TrackId!.Value));
    }

    [Fact]
    public void The_trail_keeps_the_last_twelve_bottom_centres()
    {
        var tracker = new ObjectTracker(new TrackerSettings(1, TimeSpan.FromSeconds(1)));
        IReadOnlyList<VisionObject> last = [];
        for (var i = 0; i < 20; i++)
        {
            last = tracker.Update([Obj(Det.Person(0.2 + i * 0.01, 0.8))], T0.AddSeconds(i * 0.125));
        }

        var trail = Assert.Single(last).Trail;
        Assert.Equal(ObjectTracker.TrailLength, trail.Count);
        Assert.Equal(0.39, trail[^1].X, 2);
        Assert.Equal(0.28, trail[0].X, 2);
    }

    [Fact]
    public void Tracks_are_bounded_by_the_object_limit()
    {
        var tracker = new ObjectTracker(new TrackerSettings(1, TimeSpan.FromSeconds(5)));
        var many = Enumerable.Range(0, 400).Select(i => Obj(Det.Person(0.001 + (i % 20) * 0.05, 0.05 + (i / 20) * 0.047, w: 0.01, h: 0.01))).ToList();
        var output = tracker.Update(many, T0);
        Assert.Equal(VisionLimits.MaxObjects, output.Count);
        Assert.True(tracker.TrackCount <= VisionLimits.MaxObjects);

        // Lost ones make room for new ones, the oldest first.
        tracker.Update([], T0.AddSeconds(0.1));
        var fresh = Enumerable.Range(0, 10).Select(i => Obj(Det.Car(0.1 + i * 0.08, 0.99))).ToList();
        Assert.Equal(10, tracker.Update(fresh, T0.AddSeconds(0.2)).Count);
        Assert.Equal(VisionLimits.MaxObjects, tracker.TrackCount);
    }

    [Fact]
    public void An_older_frame_is_ignored()
    {
        var tracker = new ObjectTracker(new TrackerSettings(1, TimeSpan.FromSeconds(1)));
        tracker.Update([Obj(Det.Person(0.5, 0.8))], T0.AddSeconds(1));
        Assert.Empty(tracker.Update([Obj(Det.Person(0.5, 0.8))], T0));
    }

    private static VisionObject Obj(Detection d) => new() { Detection = d };
}
