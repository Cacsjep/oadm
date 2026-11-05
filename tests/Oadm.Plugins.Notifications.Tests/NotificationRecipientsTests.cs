using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Notifications.Tests;

/// <summary>The recipient holds the target and its login, the action keeps the message (docs/spec/recipients.md).</summary>
public sealed class NotificationRecipientsTests
{
    public static TheoryData<IActionPlugin, RecipientType, string[], string[]> Actions => new()
    {
        { new SendHttpAction(), NotificationRecipients.Http, ["url", "auth", "user", "password", "token", "allowUntrusted"], ["method", "query", "headers", "bodyType", "body", "timeout", "onOff"] },
        { new SendEmailAction(), NotificationRecipients.Email, ["host", "port", "security", "allowUntrusted", "user", "password", "from", "to"], ["subject", "format", "body", "onOff"] },
        { new SendTcpAction(), NotificationRecipients.Tcp, ["host", "port", "tls", "allowUntrusted"], ["format", "message", "lineEnd", "waitAnswer", "expect", "timeout", "onOff"] },
    };

    [Theory]
    [MemberData(nameof(Actions))]
    public void Each_action_declares_its_type_and_keeps_only_the_message(IActionPlugin action, RecipientType type, string[] recipient, string[] kept)
    {
        Assert.Equal(type, Assert.Single(action.RecipientTypes));
        Assert.Equal(action.Id, type.TestActionId);
        Assert.Equal(recipient, type.Fields.Select(f => f.Key));

        var select = action.Fields[0];
        Assert.Equal(ActionFieldType.Recipient, select.Type);
        Assert.True(select.Required);
        Assert.Equal([type.Id], select.RecipientTypes);
        Assert.Equal(kept, action.Fields.Skip(1).Select(f => f.Key));
    }

    [Fact]
    public void An_empty_required_field_says_what_to_enter()
    {
        Assert.Equal("Enter a URL.", EmptyErrors(NotificationRecipients.Http)["url"]);

        var email = EmptyErrors(NotificationRecipients.Email);
        Assert.Equal("Enter a host name or IP address.", email["host"]);
        Assert.Equal("Enter an email address.", email["from"]);
        Assert.Equal("Enter an email address.", email["to"]);

        Assert.Equal("Enter a host name or IP address.", EmptyErrors(NotificationRecipients.Tcp)["host"]);
    }

    private static Dictionary<string, string> EmptyErrors(RecipientType type) =>
        ActionSettings.Check(type.Fields, new System.Text.Json.Nodes.JsonObject(), statefulRule: false);

    [Fact]
    public void Log_entry_has_no_recipient()
    {
        var action = new LogEntryAction();
        Assert.Empty(((IActionPlugin)action).RecipientTypes);
        Assert.DoesNotContain(action.Fields, f => f.Type == ActionFieldType.Recipient);
    }
}
