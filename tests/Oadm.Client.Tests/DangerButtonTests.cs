using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>Destructive confirmations get the red danger button (one rule for the host and every plugin).</summary>
public sealed class DangerButtonTests
{
    [Theory]
    [InlineData("Delete", true)]
    [InlineData("Delete all", true)]
    [InlineData("Remove", true)]
    [InlineData("Release", true)]
    [InlineData("Format", true)]
    [InlineData("Replace", false)]
    [InlineData("Install", false)]
    [InlineData("OK", false)]
    [InlineData(null, false)]
    public void Confirm_texts_that_delete_or_remove_are_destructive(string? confirmText, bool destructive) =>
        Assert.Equal(destructive, MessageWindow.IsDestructive(confirmText));

    [Fact]
    public void The_theme_has_the_danger_button_styles()
    {
        string theme = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Oadm.Theme", "OadmTheme.axaml"));
        Assert.Contains("Selector=\"Button.danger\"", theme, StringComparison.Ordinal);
        Assert.Contains("Selector=\"Button.toolbar.danger\"", theme, StringComparison.Ordinal);
        Assert.Contains("Selector=\"Button.link.danger\"", theme, StringComparison.Ordinal);
    }

    private static string RepoRoot() => Oadm.Tests.Shared.TestRepository.Root;
}
