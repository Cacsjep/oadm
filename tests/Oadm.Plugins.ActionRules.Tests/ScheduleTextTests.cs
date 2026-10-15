using Oadm.Plugins.ActionRules.Device;
using Oadm.Plugins.ActionRules.Rules;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>The iCalendar text of the recorded schedules (AXIS OS 10, 11, 12) read, described and built again.</summary>
public sealed class ScheduleTextTests
{
    [Theory]
    [InlineData("os12")]
    [InlineData("os11")]
    [InlineData("os10")]
    public void Every_recorded_schedule_reads_and_builds_back(string os)
    {
        foreach (var schedule in SoapActionEngine.ParseSchedules(RuleCamera.Fixture("GetScheduledEvents", os)))
        {
            var shape = ScheduleText.Parse(schedule.Definition);
            Assert.Equal(ScheduleText.Normalize(schedule.Definition), ScheduleText.Build(shape));
            Assert.False(string.IsNullOrWhiteSpace(ScheduleText.Describe(schedule.Definition)));
        }
    }

    [Theory]
    [InlineData("DTSTART:19700101T080000\nDTEND:19700101T180000\nRRULE:FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", "Mon to Fri 08:00 to 18:00")]
    [InlineData("DTSTART:19700101T180000\nDTEND:19700102T080000\nRRULE:FREQ=WEEKLY;BYDAY=WE,TH,FR", "Wed to Fri 18:00 to 08:00 the next day")]
    [InlineData("DTSTART:19700101T000000\nDTEND:19700102T000000\nRRULE:FREQ=WEEKLY;BYDAY=TU,FR", "Tue, Fri 00:00 to 00:00 the next day")]
    [InlineData("DTSTART:19700101T000000\nRRULE:FREQ=MINUTELY;INTERVAL=1", "Every minute")]
    [InlineData("DTSTART:19700101T000000\nRRULE:FREQ=MINUTELY;INTERVAL=120", "Every 2 hours")]
    [InlineData("DTSTART:19700105T000000\nDTEND:19700110T000000\nRRULE:FREQ=WEEKLY", "Mon 00:00 to Sat 00:00")]
    [InlineData("DTSTART:19700103T000000\nDTEND:19700105T000000\nRRULE:FREQ=WEEKLY", "Sat 00:00 to Mon 00:00")]
    [InlineData("DTSTART:19700101T000000\nRRULE:FREQ=SECONDLY;INTERVAL=1", "DTSTART:19700101T000000 RRULE:FREQ=SECONDLY;INTERVAL=1")]
    public void Schedules_read_as_the_web_interface_writes_them(string definition, string text) =>
        Assert.Equal(text, ScheduleText.Describe(definition));

    [Fact]
    public void A_weekly_range_builds_like_the_web_interface()
    {
        var shape = new ScheduleShape(ScheduleKind.Weekly, [DayOfWeek.Saturday, DayOfWeek.Sunday], TimeSpan.FromHours(22), TimeSpan.FromHours(6), 0, string.Empty);
        Assert.Equal("DTSTART:19700101T220000\nDTEND:19700102T060000\nRRULE:FREQ=WEEKLY;BYDAY=SA,SU", ScheduleText.Build(shape));
        Assert.Null(ScheduleText.Problem(shape));
        Assert.Equal("Choose at least one day.", ScheduleText.Problem(shape with { Days = [] }));
        Assert.Equal("Enter 1 to 10,080 minutes.", ScheduleText.Problem(new ScheduleShape(ScheduleKind.Every, [], default, default, 0, string.Empty)));
    }

    [Theory]
    [InlineData("Night shift", "com.axis.schedules.oadm.night-shift")]
    [InlineData("  Büro / Werktage 2 ", "com.axis.schedules.oadm.b-ro-werktage-2")]
    [InlineData("!!!", "com.axis.schedules.oadm.schedule")]
    public void Own_ids_are_the_same_for_the_same_name(string name, string id) => Assert.Equal(id, ScheduleText.OwnId(name));
}
