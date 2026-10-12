using System.Net.Http.Headers;

using Oadm.Plugins.EdgeRecordingExport.Recordings;

namespace Oadm.Plugins.EdgeRecordingExport.Tests;

/// <summary>The Edge storage answers of the AXIS P3265-V (AXIS OS 12.11, export schema 1.4) and the request URIs.</summary>
public sealed class ParsingTests
{
    [Fact]
    public void The_list_gives_every_recording_with_times_cut_to_the_range_and_the_matched_count()
    {
        var (matched, recordings) = EdgeStorageParsing.ParseList(Recorded.Read("p3265-list.xml"));

        Assert.Equal(7, matched); // numberofrecordings: the matches, the page holds 3
        Assert.Equal(3, recordings.Count);
        var first = recordings[0];
        Assert.Equal(("20261012_200925_A3A0_B8A44F631339", "SD_DISK", "1", "triggered"), (first.Id, first.DiskId, first.Source, first.Type));
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 18, 9, 30, TimeSpan.Zero), first.Start); // cut to the requested start
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 18, 9, 35, TimeSpan.Zero).AddTicks(6764740), first.Stop);
    }

    [Fact]
    public void An_empty_list_and_a_recording_that_still_records()
    {
        Assert.Equal((0, 0), (EdgeStorageParsing.ParseList(Recorded.Read("empty-list.xml")).Matched, EdgeStorageParsing.ParseList(Recorded.Read("empty-list.xml")).Recordings.Count));
        var (_, ongoing) = EdgeStorageParsing.ParseList(
            "<root><recordings numberofrecordings=\"1\"><recording diskid=\"SD_DISK\" recordingid=\"r1\" starttime=\"2026-10-12T18:09:35.776480Z\" stoptime=\"\" recordingstatus=\"recording\"/></recordings></root>");
        Assert.Null(Assert.Single(ongoing).Stop);
        Assert.Throws<FormatException>(() => EdgeStorageParsing.ParseList("<root/>"));
    }

    [Fact]
    public void The_estimate_is_kilobytes_and_an_error_carries_the_cameras_text()
    {
        Assert.Equal(2556L * 1024, EdgeStorageParsing.ParseEstimatedBytes(Recorded.Read("p3265-properties.xml")));
        var ex = Assert.Throws<EdgeStorageException>(() => EdgeStorageParsing.ParseEstimatedBytes(Recorded.Read("p3265-properties-not-found.xml")));
        Assert.Equal((110, "Recording not found"), (ex.Code, ex.Message));
        Assert.Null(EdgeStorageParsing.ParseEstimatedBytes("<ExportRecordingResponse><PropertiesSuccess><ExportProperties EstimatedFileSize=\"-1\"/></PropertiesSuccess></ExportRecordingResponse>"));
    }

    [Fact]
    public void A_disk_is_in_use_when_one_says_OK()
    {
        Assert.True(EdgeStorageParsing.HasDiskInUse(Recorded.Read("p3265-disks.xml")));
        Assert.False(EdgeStorageParsing.HasDiskInUse(Recorded.Read("no-sd-card-disks.xml"))); // SD_DISK and NetworkShare "disconnected"
    }

    [Fact]
    public void Mp4_is_taken_when_offered_else_matroska()
    {
        var formats = EdgeStorageParsing.ParseFormats(Recorded.Read("p3265-capabilities.xml"));
        Assert.Equal(["matroska", "mp4"], formats);
        Assert.Equal("mp4", EdgeStorageParsing.ChooseFormat(formats));
        Assert.Equal("matroska", EdgeStorageParsing.ChooseFormat(["matroska"]));
        Assert.Equal((".mp4", ".mkv"), (EdgeStorageRequests.Extension("mp4"), EdgeStorageRequests.Extension("matroska")));
    }

    [Fact]
    public void The_requests_carry_the_cameras_time_format_and_escape_the_values()
    {
        var start = new DateTimeOffset(2026, 10, 12, 20, 9, 25, TimeSpan.FromHours(2)).AddTicks(7431580);
        Assert.Equal(
            "axis-cgi/record/list.cgi?recordingid=all&sortorder=ascending&startatresultnumber=1000&maxnumberofresults=1000&starttime=2026-10-12T18%3A09%3A25.743158Z",
            EdgeStorageRequests.ListUri(start, null, 1000, 1000));
        Assert.Equal(
            "axis-cgi/record/export/exportrecording.cgi?schemaversion=1&recordingid=a%26b&diskid=SD_DISK&exportformat=mp4&starttime=2026-10-12T18%3A09%3A25.743158Z",
            EdgeStorageRequests.ExportUri("a&b", "SD_DISK", start, null, "mp4"));
    }

    [Theory]
    [InlineData("attachment; filename=\"20261012_200927_20261012_200933.mp4\"", "20261012_200927_20261012_200933.mp4")]
    [InlineData("attachment; filename=\"../../evil.mp4\"", "evil.mp4")]
    [InlineData("attachment; filename=\"..\"", null)]
    [InlineData("attachment", null)]
    public void The_file_name_comes_from_the_header_without_folders(string header, string? expected) =>
        Assert.Equal(expected, EdgeStorageParsing.FileName(ContentDispositionHeaderValue.Parse(header)));

    [Fact]
    public void Samples_spread_over_the_list_with_both_ends()
    {
        Assert.Equal([0, 1, 2], EdgeRecordingService.Samples(3, 40));
        var samples = EdgeRecordingService.Samples(8640, 40).ToList();
        Assert.Equal(40, samples.Count);
        Assert.Equal((0, 8639), (samples[0], samples[^1]));
    }
}
