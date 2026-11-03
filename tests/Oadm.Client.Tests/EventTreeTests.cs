using System.Diagnostics;

using Oadm.Client.EventRules;
using Oadm.Contracts.V1;

using Xunit.Abstractions;

namespace Oadm.Client.Tests;

/// <summary>The event tree of the rule dialog's event picker: folders, names in their folder, search, opening.</summary>
public sealed class EventTreeTests(ITestOutputHelper output)
{
    private static EventTypeOption Event(string id, string name, string group, params string[] path)
    {
        var entry = new EventTypeEntry { Id = id, DisplayName = name, Group = group, Available = true, Description = name + "." };
        entry.Path.AddRange(path);
        return new EventTypeOption(entry);
    }

    private static readonly EventTypeOption[] Catalog =
    [
        Event("a", "Device unreachable", "Device status", "Devices", "Selected devices"),
        Event("b", "Any device: unreachable", "Any device", "Devices", "Any device"),
        Event("c", "Any device: clock off", "Any device", "Devices", "Any device"),
        Event("d", "Certificate enrolled", "EST server", "Certificates", "EST server"),
        Event("e", "Any device: certificate expired", "Certificates", "Certificates", "Any device"),
        Event("f", "Compliance: HTTPS only", "Compliance", "Devices", "Compliance"),
        Event("g", "Site offline: Yard", "Sites", "Sites"),
        Event("h", "Gate contact", "Device triggers", "Devices", "Device triggers"),
        Event("i", "Older plugin event", "Image Health"), // no path: its group is its folder
    ];

    private static List<string> Lines(EventTree tree) =>
        [.. tree.Rows.Select(r => new string(' ', r.Depth * 2) + (r is EventTreeFolder f ? (f.IsOpen ? "v " : "> ") : "- ") + r.Text)];

    [Fact]
    public void Folders_are_ordered_and_the_top_ones_start_open()
    {
        var tree = new EventTree(Catalog);

        Assert.Equal(
        [
            "v Devices",
            "  > Selected devices",
            "  > Any device",
            "  > Compliance",
            "  > Device triggers",
            "v Certificates",
            "  > Any device",
            "  > EST server",
            "v Sites",
            "  - Site offline: Yard",
            "v Image Health",
            "  - Older plugin event",
        ], Lines(tree));
    }

    [Fact]
    public void The_area_names_rank_only_at_their_level()
    {
        var tree = new EventTree(
        [
            Event("1", "Open", "Dashboards", "Dashboards", "Sites"),
            Event("2", "Close", "Dashboards", "Dashboards", "Devices"),
            Event("3", "Stop", "Dashboards", "Dashboards", "Any device"),
            Event("4", "Ring", "Dashboards", "Dashboards", "Certificates"),
            Event("5", "Alarm", "Alarms", "Alarms"),
            Event("6", "Device unreachable", "Device status", "Devices", "Selected devices"),
        ]);

        Assert.Equal(
            ["v Devices", "  > Selected devices", "v Alarms", "  - Alarm", "v Dashboards", "  > Any device", "  > Certificates", "  > Devices", "  > Sites"],
            Lines(tree));
    }

    [Fact]
    public void An_event_shows_without_the_folder_its_name_starts_with()
    {
        var tree = new EventTree(Catalog);
        tree.Toggle(tree.Rows.OfType<EventTreeFolder>().First(f => f.Text == "Any device"));
        tree.Toggle(tree.Rows.OfType<EventTreeFolder>().Single(f => f.Text == "Compliance"));

        var events = tree.Rows.OfType<EventTreeEvent>().Select(e => e.Text).ToList();
        Assert.Contains("Unreachable", events);
        Assert.Contains("Clock off", events);
        Assert.Contains("HTTPS only", events);
        Assert.Contains("Site offline: Yard", events); // "Site offline" is not its folder
        Assert.Equal("Devices › Any device › Unreachable", EventTree.PathText(Catalog[1]));
        Assert.Equal("Image Health › Older plugin event", EventTree.PathText(Catalog[8]));
    }

    [Fact]
    public void A_turned_off_event_says_so()
    {
        var entry = new EventTypeEntry { Id = "x", DisplayName = "Blur detected", Group = "Image Health", Available = false };
        Assert.Equal("Blur detected (turned off)", EventTree.TextIn(new EventTypeOption(entry), "Image Health"));
    }

    [Fact]
    public void Toggle_opens_and_closes_a_folder()
    {
        var tree = new EventTree(Catalog);
        var single = tree.Rows.OfType<EventTreeFolder>().First(f => f.Text == "Selected devices");

        tree.Toggle(single);
        Assert.Contains(tree.Rows, r => r is EventTreeEvent { Text: "Device unreachable" });
        tree.Toggle(single);
        Assert.DoesNotContain(tree.Rows, r => r is EventTreeEvent { Text: "Device unreachable" });

        var devices = tree.Rows.OfType<EventTreeFolder>().First(f => f.Text == "Devices");
        tree.Toggle(devices);
        Assert.DoesNotContain(tree.Rows, r => r.Depth > 0 && tree.Rows.IndexOf(r) < tree.Rows.IndexOf(tree.Rows.OfType<EventTreeFolder>().First(f => f.Text == "Certificates")));
    }

    [Fact]
    public void Search_shows_the_matches_with_their_folders_open()
    {
        var tree = new EventTree(Catalog) { Query = "unreach" };

        Assert.Equal(
        [
            "v Devices",
            "  v Selected devices",
            "    - Device unreachable",
            "  v Any device",
            "    - Unreachable",
        ], Lines(tree));

        tree.Query = "EST"; // a folder's name finds its events
        Assert.Equal(["Certificate enrolled"], tree.Rows.OfType<EventTreeEvent>().Select(e => e.Text));

        tree.Query = "no such event";
        Assert.True(tree.IsEmpty);
        Assert.Empty(tree.Rows);

        tree.Query = string.Empty; // back to the folders as they were
        Assert.False(tree.IsEmpty);
        Assert.Equal("  > Selected devices", Lines(tree)[1]);
    }

    [Fact]
    public void Reveal_opens_the_folders_of_the_chosen_event()
    {
        var tree = new EventTree(Catalog);

        var row = tree.Reveal(Catalog[3]);

        Assert.NotNull(row);
        Assert.Contains(row, tree.Rows);
        Assert.Null(tree.Reveal(null));
        Assert.Null(tree.Reveal(Event("z", "Not in the tree", "X")));
    }

    [Fact]
    public void An_older_server_without_folders_groups_by_the_group()
    {
        var tree = new EventTree([Event("a", "Device unreachable", "Device status"), Event("b", "Gate contact", "Device triggers")]);

        Assert.Equal(["v Device status", "  - Device unreachable", "v Device triggers", "  - Gate contact"], Lines(tree));
    }

    [Fact]
    [Trait("Category", "Perf")]
    public void Many_events_build_and_filter_fast()
    {
        // 100,000 events (device triggers and compliance checks a large installation could have) in a few folders.
        var options = Enumerable.Range(0, 100_000)
            .Select(i => Event("t" + i, "Trigger " + i, "Device triggers", "Devices", i % 2 == 0 ? "Device triggers" : "Compliance"))
            .ToList();
        var watch = Stopwatch.StartNew();
        var tree = new EventTree(options);
        var built = watch.Elapsed;
        tree.Reveal(options[^1]);
        tree.Query = "trigger 9999";
        var searched = watch.Elapsed - built;
        output.WriteLine($"Built in {built.TotalMilliseconds:0} ms, revealed and searched in {searched.TotalMilliseconds:0} ms.");

        Assert.Equal(11, tree.Rows.OfType<EventTreeEvent>().Count()); // 9999 and 99990..99999
        Assert.True(built < TimeSpan.FromSeconds(2.5), $"Building took {built.TotalMilliseconds:0} ms.");
        Assert.True(searched < TimeSpan.FromSeconds(1), $"Searching took {searched.TotalMilliseconds:0} ms.");
    }
}
