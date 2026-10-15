using Oadm.Plugins.ActionRules.Device;
using Oadm.Plugins.ActionRules.Rules;

namespace Oadm.Plugins.ActionRules.Tests;

/// <summary>The recorded SOAP answers of AXIS OS 12.11 (10.0.0.202) and 10.12 (10.0.0.200) and the filter rules.</summary>
public sealed class ParsingTests
{
    [Fact]
    public void Os12_rule_reads_with_its_action_and_recipient_parameters()
    {
        var configs = SoapActionEngine.ParseConfigurations(RuleCamera.Fixture("GetActionConfigurations"));
        var rules = SoapActionEngine.ParseRules(RuleCamera.Fixture("GetActionRules"), configs);

        var rule = Assert.Single(rules);
        Assert.Equal(("4", "test", true), (rule.Id, rule.Name, rule.Enabled));
        Assert.Equal("tnsaxis:CameraApplicationPlatform/faceverifier/FaceUnknown", rule.StartEvent!.Topic);
        Assert.Empty(rule.Conditions);
        Assert.Equal("com.axis.action.fixed.notification.tcp", rule.Action.Template);
        Assert.Equal("4", rule.Action.ConfigurationId);
        Assert.Equal(["port", "qos", "message", "host"], rule.Action.Parameters.Select(p => p.Name));
        Assert.Equal("192.168.1.5", rule.Action.Get("host"));
    }

    [Theory]
    [InlineData("os12", 52)]
    [InlineData("os10", 49)]
    public void Templates_differ_by_firmware(string os, int count)
    {
        var templates = SoapActionEngine.ParseActionTemplates(RuleCamera.Fixture("GetActionTemplates", os));
        var recipients = SoapActionEngine.ParseRecipientTemplates(RuleCamera.Fixture("GetRecipientTemplates", os));

        Assert.Equal(count, templates.Count);
        var tcp = templates.Single(t => t.Token == "com.axis.action.unlimited.notification.tcp");
        Assert.Equal(("com.axis.recipient.tcp", true), (tcp.RecipientTemplate, tcp.IsWhileActive));
        Assert.Equal(["message", "period"], tcp.Parameters);
        Assert.Equal(["host", "port", "qos"], recipients.Single(r => r.Token == "com.axis.recipient.tcp").Parameters);
        Assert.Equal(os == "os12", templates.Single(t => t.Token == "com.axis.action.fixed.set_overlay").Parameters.Contains("index"));
    }

    [Fact]
    public void Os10_condition_topic_ends_with_its_marker_and_compares_equal()
    {
        var doc = RuleCamera.Fixture("GetActionRules-conditions", "os10");
        var rules = SoapActionEngine.ParseRules(doc, new Dictionary<string, RuleAction> { ["1"] = new("com.axis.action.unlimited.notification.tcp", []) });

        var condition = Assert.Single(Assert.Single(rules).Conditions);
        Assert.EndsWith("//.", condition.Topic, StringComparison.Ordinal);
        Assert.Equal("PT5S", rules[0].Timeout);
        var os12 = new EventFilter(
            "tns1:Device/tnsaxis:IO/VirtualInput",
            "boolean(//SimpleItem[@Name=\"active\" and @Value=\"1\"]) and boolean(//SimpleItem[@Name=\"port\" and @Value=\"20\"])");
        Assert.Equal(RuleText.Key(os12), RuleText.Key(condition));
    }

    [Fact]
    public void Topic_names_come_from_the_event_list()
    {
        var names = SoapActionEngine.ParseTopicNames(RuleCamera.Fixture("GetEventInstances"));

        Assert.Equal("Schedule: Scheduled event", names["tns1:UserAlarm/tnsaxis:Recurring/Interval"]);
        Assert.True(names.ContainsKey("tns1:Device/tnsaxis:IO/VirtualInput"));
    }

    [Fact]
    public void Schedules_and_recipients_read()
    {
        var schedules = SoapActionEngine.ParseSchedules(RuleCamera.Fixture("GetScheduledEvents"));
        var recipients = SoapActionEngine.ParseRecipients(RuleCamera.Fixture("GetRecipientConfigurations"));

        Assert.Contains(schedules, s => s.Id == "com.axis.schedules.office_hours" && s.IsBuiltIn && s.Definition.Contains("BYDAY=MO,TU,WE,TH,FR", StringComparison.Ordinal));
        Assert.False(schedules.Single(s => s.Id == "com.axis.schedules.genid.id-1").IsBuiltIn);
        var ftp = Assert.Single(recipients);
        Assert.Equal(("asdasd", "com.axis.recipient.ftp"), (ftp.Name, ftp.Template));
    }

    [Fact]
    public void Filters_describe_schedules_and_values()
    {
        var schedule = new EventFilter(RuleText.ScheduleIntervalTopic, "boolean(//SimpleItem[@Name=\"id\" and @Value=\"com.axis.schedules.office_hours\"]) and boolean(//SimpleItem[@Name=\"active\" and @Value=\"1\"])");
        var input = new EventFilter("tns1:Device/tnsaxis:IO/VirtualInput", "boolean(//SimpleItem[@Name=\"port\" and @Value=\"20\"])");

        Assert.Equal("com.axis.schedules.office_hours", RuleText.ScheduleId(schedule));
        Assert.Equal("Schedule: Office Hours", RuleText.Describe(schedule, null, new Dictionary<string, string> { ["com.axis.schedules.office_hours"] = "Office Hours" }));
        Assert.Equal("Virtual input (port 20)", RuleText.Describe(input, new Dictionary<string, string> { ["tns1:Device/tnsaxis:IO/VirtualInput"] = "Virtual input" }));
        Assert.Equal("Device / IO / VirtualInput (port 20)", RuleText.Describe(input));
    }

    [Theory]
    [InlineData("PT10S", 10)]
    [InlineData("PT1M30S", 90)]
    [InlineData("P1DT1H", 90000)]
    [InlineData("bad", null)]
    public void Timeouts_read(string text, int? seconds) => Assert.Equal(seconds, RuleText.TimeoutSeconds(text));

    [Theory]
    [InlineData("password", true)]
    [InlineData("proxy_password", true)]
    [InlineData("ssh_private_key_passphrase", true)]
    [InlineData("message", false)]
    [InlineData("host", false)]
    public void Secret_parameters(string name, bool secret) => Assert.Equal(secret, RuleText.IsSecret(name));
}
