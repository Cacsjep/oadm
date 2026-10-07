namespace Oadm.Plugins.Users.Tests;

public sealed class CredentialRulesTests
{
    [Theory]
    [InlineData("joe")]
    [InlineData("A1")]
    [InlineData("abcdefghij1234")]
    public void Valid_user_names(string name) => Assert.Null(CredentialRules.ValidateUserName(name));

    [Theory]
    [InlineData("")]
    [InlineData("abcdefghij12345")]
    [InlineData("joe doe")]
    [InlineData("joe.doe")]
    [InlineData("jöe")]
    [InlineData("joe;rm")]
    public void Invalid_user_names(string name) => Assert.NotNull(CredentialRules.ValidateUserName(name));

    [Theory]
    [InlineData("a", PassphrasePolicy.None)]
    [InlineData("with space and !~", PassphrasePolicy.None)]
    [InlineData("fifteen-chars-x", PassphrasePolicy.Length)]
    [InlineData("Abcdefghij1!", PassphrasePolicy.Complex)]
    public void Valid_passwords(string password, PassphrasePolicy policy) =>
        Assert.Null(CredentialRules.ValidatePassword(password, policy));

    [Theory]
    [InlineData("", PassphrasePolicy.None)]
    [InlineData("tab\there", PassphrasePolicy.None)]
    [InlineData("pässword", PassphrasePolicy.None)]
    [InlineData("fourteen-chars", PassphrasePolicy.Length)]
    [InlineData("Abcdefghi1!", PassphrasePolicy.Complex)]
    [InlineData("abcdefghij1!", PassphrasePolicy.Complex)]
    [InlineData("ABCDEFGHIJ1!", PassphrasePolicy.Complex)]
    [InlineData("Abcdefghijk!", PassphrasePolicy.Complex)]
    [InlineData("Abcdefghijk1", PassphrasePolicy.Complex)]
    public void Invalid_passwords(string password, PassphrasePolicy policy)
    {
        var reason = CredentialRules.ValidatePassword(password, policy);
        Assert.NotNull(reason);
        if (password.Length > 0)
        {
            Assert.DoesNotContain(password, reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Passwords_longer_than_64_are_refused()
    {
        Assert.Null(CredentialRules.ValidatePassword(new string('a', 64), PassphrasePolicy.None));
        Assert.NotNull(CredentialRules.ValidatePassword(new string('a', 65), PassphrasePolicy.None));
    }

    [Theory]
    [InlineData("none", PassphrasePolicy.None)]
    [InlineData("LENGTH", PassphrasePolicy.Length)]
    [InlineData("complex", PassphrasePolicy.Complex)]
    [InlineData("future", PassphrasePolicy.None)]
    [InlineData(null, PassphrasePolicy.None)]
    public void Policy_names(string? value, PassphrasePolicy expected) => Assert.Equal(expected, CredentialRules.ParsePolicy(value));

    [Fact]
    public void Hints_name_the_policy_minimum()
    {
        Assert.Contains("15", CredentialRules.Hint(PassphrasePolicy.Length), StringComparison.Ordinal);
        Assert.Contains("12", CredentialRules.Hint(PassphrasePolicy.Complex), StringComparison.Ordinal);
    }

    [Fact]
    public void Payload_text_never_contains_the_password()
    {
        var payload = new UsersPayload { Mode = UsersMode.Add, UserName = "joe", Password = "TopSecret123!", Role = UserRole.Viewer };
        Assert.DoesNotContain("TopSecret123!", payload.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Malformed_payload_error_does_not_echo_the_json()
    {
        var ex = Assert.Throws<ArgumentException>(() => UsersJson.ParsePayload("{\"password\":\"TopSecret123!\",\"mode\":42x"));
        Assert.DoesNotContain("TopSecret123!", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Payload_round_trips_as_camel_case_json()
    {
        var json = UsersJson.Serialize(new UsersPayload { Mode = UsersMode.Change, UserName = "joe", Role = UserRole.Operator, Ptz = true, ChangeRole = true });
        Assert.Contains("\"mode\":\"change\"", json, StringComparison.Ordinal);
        Assert.Contains("\"role\":\"operator\"", json, StringComparison.Ordinal);
        var back = UsersJson.ParsePayload(json);
        Assert.Equal(UsersMode.Change, back.Mode);
        Assert.Equal(UserRole.Operator, back.Role);
        Assert.True(back.Ptz);
        Assert.True(back.ChangeRole);
    }
}
