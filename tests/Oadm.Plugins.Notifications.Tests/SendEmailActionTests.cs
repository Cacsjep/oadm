using System.Text.Json.Nodes;

namespace Oadm.Plugins.Notifications.Tests;

public sealed class SendEmailActionTests
{
    private static JsonObject Settings(int port) => new()
    {
        ["host"] = "127.0.0.1",
        ["port"] = port,
        ["security"] = "none",
        ["from"] = "oadm@example.com",
        ["to"] = "security@example.com, {hostName}@example.com",
        ["subject"] = "{rule}: {event} on {device}",
        ["format"] = "text",
        ["body"] = "Hello {serial}, object {objectId} at {time}.",
    };

    [Fact]
    public async Task Sends_a_text_mail_with_the_values_filled_in()
    {
        await using var smtp = new MiniSmtpServer();
        var context = new TestContext(new Dictionary<string, string> { ["objectId"] = "42" }, new TestDevice());

        await new SendEmailAction().ExecuteAsync(context, Run.Invocation(Settings(smtp.Port)), CancellationToken.None);

        var mail = Assert.Single(smtp.Mails);
        Assert.Equal("oadm@example.com", mail.From);
        Assert.Equal(["security@example.com", "cam-gate@example.com"], mail.To);
        Assert.Contains("Subject: Gate rule: Person in the yard on 10.0.0.48 (AXIS P3265-V)", mail.Data, StringComparison.Ordinal);
        Assert.Contains("Hello B8A44F631339, object 42 at 2026-10-11", mail.Data, StringComparison.Ordinal);
        Assert.Null(mail.Auth);
        Assert.Equal(["Sent to security@example.com, cam-gate@example.com"], context.Lines);
    }

    [Fact]
    public async Task Html_escapes_the_values_and_logs_in_when_a_user_is_set()
    {
        await using var smtp = new MiniSmtpServer();
        var settings = Settings(smtp.Port);
        settings["format"] = "html";
        settings["body"] = "<p>Object <b>{objectId}</b></p>";
        settings["to"] = "security@example.com";
        settings["user"] = "mailer";
        settings["password"] = "pw";
        var context = new TestContext(new Dictionary<string, string> { ["objectId"] = "<script>" });

        await new SendEmailAction().ExecuteAsync(context, Run.Invocation(settings), CancellationToken.None);

        var mail = Assert.Single(smtp.Mails);
        Assert.Contains("&lt;script&gt;", mail.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("<b><script>", mail.Data, StringComparison.Ordinal);
        Assert.Contains("text/plain", mail.Data, StringComparison.Ordinal); // a plain-text part as well
        Assert.Equal("|mailer|pw", mail.Auth);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_fails_with_a_plain_message()
    {
        var settings = Settings(1); // nothing listens on port 1
        settings["to"] = "security@example.com";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SendEmailAction().ExecuteAsync(new TestContext(), Run.Invocation(settings), CancellationToken.None));
        Assert.StartsWith("127.0.0.1:1 cannot be reached", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validation_checks_the_addresses_unless_they_hold_placeholders()
    {
        var action = new SendEmailAction();
        var settings = Settings(25);
        settings["from"] = "not an address";
        settings["to"] = "a@example.com; nope";
        var errors = action.Validate(Run.Element(settings), statefulRule: false);
        Assert.Equal("Enter an email address.", errors["from"]);
        Assert.Equal("Enter email addresses separated by commas.", errors["to"]);

        settings["from"] = "OADM <oadm@example.com>";
        settings["to"] = "{hostName}@example.com";
        Assert.Empty(action.Validate(Run.Element(settings), statefulRule: false));
    }
}
