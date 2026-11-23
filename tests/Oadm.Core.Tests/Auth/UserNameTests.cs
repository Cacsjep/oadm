using Oadm.Core.Auth;

namespace Oadm.Core.Tests;

/// <summary>User names (docs/spec/mcp.md): "MCP &lt;name&gt;" is how an access key acts, so no user may be named like that.</summary>
public sealed class UserNameTests
{
    [Theory]
    [InlineData("MCP bot")]
    [InlineData("mcp bot")]
    [InlineData(" Mcp Assistant")]
    public void AUserNameLikeAnAccessKeyIsRefused(string name)
    {
        var refused = Assert.Throws<AuthException>(() => UserStore.ValidateUserName(name));
        Assert.Contains("access keys", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mcpbot")]
    [InlineData("MCP")]
    [InlineData("anna")]
    public void OtherNamesPass(string name) => Assert.Equal(name, UserStore.ValidateUserName(name));
}
