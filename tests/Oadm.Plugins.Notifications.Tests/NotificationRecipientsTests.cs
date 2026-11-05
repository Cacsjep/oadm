using System.Text.Json.Nodes;

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

    [Theory]
    [InlineData("https://hooks.example.com/in?a=1", null)]
    [InlineData("http://127.0.0.1:8080/acs", null)]
    [InlineData("https://[fd00::1]/in", null)]
    [InlineData("http://127.0.0.1/acs asdasd", ActionFormats.EnterUrlWithoutSpaces)]
    [InlineData("ftp://example.com", ActionFormats.EnterUrl)]
    [InlineData("example.com/in", ActionFormats.EnterUrl)]
    [InlineData("http://", ActionFormats.EnterUrl)]
    [InlineData("http://999.1.1.1/in", ActionFormats.EnterUrl)]
    [InlineData("http://example.com:99999/", ActionFormats.EnterUrl)]
    [InlineData("https://hooks.example.com/{serial}", ActionFormats.EnterFixedUrl)]
    [InlineData("{webUrl}", ActionFormats.EnterFixedUrl)]
    [InlineData("https://user:secret@example.com/in", ActionFormats.EnterUrlWithoutLogin)]
    public void The_url_of_an_http_recipient_is_checked(string url, string? error) =>
        Assert.Equal(error, Errors(NotificationRecipients.Http, new JsonObject { ["url"] = url, ["auth"] = "none" }).GetValueOrDefault("url"));

    [Fact]
    public void An_http_login_needs_its_user_name_or_token()
    {
        Assert.Equal("Enter the user name.", Errors(NotificationRecipients.Http, new JsonObject { ["auth"] = "basic" })["user"]);
        Assert.Equal("Enter the user name.", Errors(NotificationRecipients.Http, new JsonObject { ["auth"] = "digest" })["user"]);
        Assert.Equal("Enter the token.", Errors(NotificationRecipients.Http, new JsonObject { ["auth"] = "bearer" })["token"]);
        Assert.DoesNotContain("token", ActionSettings.Check(NotificationRecipients.Http.Fields, new JsonObject { ["auth"] = "bearer" }, statefulRule: true, ["token"]).Keys);
    }

    [Theory]
    [InlineData("smtp.office365.com", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("fd00::1", true)]
    [InlineData("localhost", true)]
    [InlineData("mail server", false)]
    [InlineData("http://mail", false)]
    [InlineData("mail:25", false)]
    [InlineData("999.1.1.1", false)]
    [InlineData("{host}", false)]
    public void The_host_of_email_and_tcp_recipients_is_checked(string host, bool valid)
    {
        foreach (var type in new[] { NotificationRecipients.Email, NotificationRecipients.Tcp })
        {
            Assert.Equal(valid ? null : ActionFormats.EnterHost, Errors(type, new JsonObject { ["host"] = host }).GetValueOrDefault("host"));
        }
    }

    [Theory]
    [InlineData("oadm@example.com", true)]
    [InlineData("OADM <oadm@example.com>", true)]
    [InlineData("oadm@mail", true)]
    [InlineData("abc", false)]
    [InlineData("a@", false)]
    [InlineData("@example.com", false)]
    [InlineData("a b@example.com", false)]
    [InlineData("a@example com", false)]
    [InlineData("a@b@example.com", false)]
    [InlineData("<oadm@example.com", false)]
    [InlineData("{hostName}@example.com", false)]
    [InlineData("a@{domain}", false)]
    public void The_from_address_is_checked(string from, bool valid) =>
        Assert.Equal(valid ? null : ActionFormats.EnterEmail, Errors(NotificationRecipients.Email, new JsonObject { ["from"] = from }).GetValueOrDefault("from"));

    [Theory]
    [InlineData("a@example.com", true)]
    [InlineData("a@example.com, Team <b@example.com>; c@example.com,", true)]
    [InlineData("a@example.com, nope", false)]
    [InlineData("a@example.com b@example.com", false)]
    [InlineData(" , ", false)]
    public void The_to_addresses_are_checked(string to, bool valid) =>
        Assert.Equal(valid ? null : ActionFormats.EnterEmailList, Errors(NotificationRecipients.Email, new JsonObject { ["to"] = to }).GetValueOrDefault("to"));

    [Fact]
    public void Addresses_the_form_takes_are_also_taken_by_the_action()
    {
        var settings = new JsonObject { ["from"] = "OADM <oadm@example.com>", ["to"] = "a@example.com, Team <b@example.com>; oadm@mail," };
        var errors = Errors(NotificationRecipients.Email, settings);
        Assert.False(errors.ContainsKey("from"));
        Assert.False(errors.ContainsKey("to"));
        Assert.Empty(new SendEmailAction().Validate(System.Text.Json.JsonSerializer.SerializeToElement(settings), statefulRule: true));
    }

    private static Dictionary<string, string> Errors(RecipientType type, JsonObject values) =>
        ActionSettings.Check(type.Fields, values, statefulRule: true);

    private static Dictionary<string, string> EmptyErrors(RecipientType type) =>
        ActionSettings.Check(type.Fields, new JsonObject(), statefulRule: false);

    [Fact]
    public void Log_entry_has_no_recipient()
    {
        var action = new LogEntryAction();
        Assert.Empty(((IActionPlugin)action).RecipientTypes);
        Assert.DoesNotContain(action.Fields, f => f.Type == ActionFieldType.Recipient);
    }
}
