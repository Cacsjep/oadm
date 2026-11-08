using System.Text.Json;

using Oadm.Plugins.Vision.Model;

namespace Oadm.Plugins.Vision.Tests.Tracking;

/// <summary>A recorded box sequence of Fixtures/Tracks: per frame its time and detections, each with the person it shows.</summary>
internal sealed record TrackFixture(string Description, int Fps, IReadOnlyList<TrackFrame> Frames)
{
    public static readonly DateTimeOffset Start = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    public static TrackFixture Load(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Tracks", name);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var frames = new List<TrackFrame>();
        foreach (var f in root.GetProperty("frames").EnumerateArray())
        {
            var dets = new List<(int Truth, Detection Detection)>();
            foreach (var d in f.GetProperty("detections").EnumerateArray())
            {
                var b = d.GetProperty("box");
                var box = new RelativeBox(b[0].GetDouble(), b[1].GetDouble(), b[2].GetDouble(), b[3].GetDouble());
                dets.Add((d.GetProperty("truth").GetInt32(),
                    new Detection(d.GetProperty("classId").GetInt32(), d.GetProperty("class").GetString()!, d.GetProperty("score").GetDouble(), box)));
            }

            frames.Add(new TrackFrame(Start + TimeSpan.FromSeconds(f.GetProperty("t").GetDouble()), dets));
        }

        return new TrackFixture(root.GetProperty("description").GetString()!, root.GetProperty("fps").GetInt32(), frames);
    }
}

internal sealed record TrackFrame(DateTimeOffset At, IReadOnlyList<(int Truth, Detection Detection)> Detections)
{
    public IReadOnlyList<VisionObject> Objects => Detections.Select(d => new VisionObject { Detection = d.Detection }).ToList();

    public IReadOnlyList<Detection> Plain => Detections.Select(d => d.Detection).ToList();
}
