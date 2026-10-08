using System.Xml.Linq;

using Oadm.Plugins.MetadataMonitor.Parsing;

namespace Oadm.Plugins.MetadataMonitor.Tests;

/// <summary><see cref="MetadataParser"/> with the recorded 10.0.0.48 stream and hand-made documents.</summary>
public sealed class ParserTests
{
    [Fact]
    public void Recorded_stream_parses_into_one_message_per_notification()
    {
        var documents = RecordedEvents.Documents;
        var messages = documents.SelectMany(MetadataParser.Parse).ToList();

        Assert.Empty(MetadataParser.Parse(documents[0])); // the first document is an empty MetadataStream
        Assert.Equal(documents.Count - 1, messages.Count);
        Assert.All(messages, m => Assert.Equal(MessageCategories.Event, m.Category));
        Assert.All(messages, m => Assert.Equal("Initialized", m.Operation));
        Assert.All(messages, m => Assert.NotNull(m.UtcTime));
        Assert.DoesNotContain(messages, m => m.Topic.Contains("tns1:", StringComparison.Ordinal) || m.Topic.Contains("tnsaxis:", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.Topic == "Storage/Alert");
        Assert.Contains(messages, m => m.Topic == "Device/IO/VirtualInput");

        var storage = messages.First(m => m.Topic == "Storage/Alert" && m.Info.Contains("NetworkShare", StringComparison.Ordinal));
        Assert.Equal("[INIT] disk_id = NetworkShare; overall_health = -3; alert = 0; temperature = -3; wear = -3;", storage.Info);
        Assert.Equal(["disk_id", "overall_health", "alert", "temperature", "wear"], storage.Items.Select(i => i.Name));

        // The notification's XML stands on its own (namespaces declared) and is the notification only.
        var element = XElement.Parse(storage.Xml);
        Assert.Equal("NotificationMessage", element.Name.LocalName);
        Assert.Contains("tnsaxis:Storage/Alert", storage.Xml, StringComparison.Ordinal);
        foreach (var m in messages)
        {
            Console.WriteLine($"{m.Topic} | {m.Info}");
        }
    }

    [Fact]
    public void Virtual_input_info_text_matches_the_axis_tool()
    {
        var message = Assert.Single(MetadataParser.Parse(RecordedEvents.Notification("tns1:Device/tnsaxis:IO/VirtualInput", "Initialized", 33, 0)));

        Assert.Equal("Device/IO/VirtualInput", message.Topic);
        Assert.Equal("[INIT] port = 33; active = 0;", message.Info);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 9, 30, 0, TimeSpan.Zero), message.UtcTime);
        Assert.Equal("Initialized", message.Operation);
    }

    [Theory]
    [InlineData("Changed", "[CHANGED] port = 1; active = 1;")]
    [InlineData("Deleted", "[DELETED] port = 1; active = 1;")]
    public void Property_operations_are_tagged(string operation, string info) =>
        Assert.Equal(info, Assert.Single(MetadataParser.Parse(RecordedEvents.Notification("tns1:Device/tnsaxis:IO/Port", operation, 1, 1))).Info);

    [Fact]
    public void Prefixes_are_resolved_by_namespace_not_by_name()
    {
        const string xml = """
            <tt:MetadataStream xmlns:tt="http://www.onvif.org/ver10/schema"><tt:Event>
            <n:NotificationMessage xmlns:n="http://docs.oasis-open.org/wsn/b-2" xmlns:o="http://www.onvif.org/ver10/topics" xmlns:ax="http://www.axis.com/2009/event/topics" xmlns:acap="http://example.com/acap">
            <n:Topic>o:VideoSource/ax:Tampering</n:Topic><n:Message><tt:Message UtcTime="2026-10-08T09:30:00Z"><tt:Source><tt:SimpleItem Name="channel" Value="1"/></tt:Source><tt:Data><tt:SimpleItem Name="tampering" Value="1"/></tt:Data></tt:Message></n:Message></n:NotificationMessage>
            <n:NotificationMessage xmlns:n="http://docs.oasis-open.org/wsn/b-2" xmlns:acap="http://example.com/acap">
            <n:Topic>acap:MyApp/Alarm</n:Topic><n:Message><tt:Message UtcTime="bad"/></n:Message></n:NotificationMessage>
            </tt:Event></tt:MetadataStream>
            """;
        var messages = MetadataParser.Parse(xml);

        Assert.Equal(2, messages.Count);
        Assert.Equal("VideoSource/Tampering", messages[0].Topic);
        Assert.Equal("channel = 1; tampering = 1;", messages[0].Info); // no operation: no tag
        Assert.Null(messages[0].Operation);
        Assert.Equal("acap:MyApp/Alarm", messages[1].Topic); // unknown namespaces keep their prefix
        Assert.Null(messages[1].UtcTime);
        Assert.Equal(string.Empty, messages[1].Info);
    }

    [Fact]
    public void Malformed_xml_is_one_invalid_message_with_the_raw_text()
    {
        const string broken = "<?xml version=\"1.0\"?><tt:MetadataStream xmlns:tt=\"x\"><tt:Event>";
        var message = Assert.Single(MetadataParser.Parse(broken));

        Assert.Equal(MessageCategories.Invalid, message.Category);
        Assert.Equal(broken, message.Xml);
        Assert.StartsWith("Malformed XML: ", message.Info, StringComparison.Ordinal);
        Assert.Equal(string.Empty, message.Topic);
    }

    [Fact]
    public void Dtds_are_refused()
    {
        const string xml = "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"boom\">]><x>&e;</x>";
        Assert.Equal(MessageCategories.Invalid, Assert.Single(MetadataParser.Parse(xml)).Category);
    }

    [Fact]
    public void Huge_xml_is_cut_for_the_page()
    {
        var broken = "<x>" + new string('a', MetadataMonitorPluginInfo.MaxXmlCharacters + 100);
        Assert.Equal(MetadataMonitorPluginInfo.MaxXmlCharacters, Assert.Single(MetadataParser.Parse(broken)).Xml.Length);
    }
}
