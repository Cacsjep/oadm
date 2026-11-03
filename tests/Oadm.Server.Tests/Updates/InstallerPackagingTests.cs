using System.Xml.Linq;

using Oadm.Tests.Shared;

namespace Oadm.Server.Tests.Updates;

/// <summary>The installers keep the setup type on a silent update (docs/spec/self-update.md rule 10).</summary>
public sealed class InstallerPackagingTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

    [Fact]
    public void TheMsiSetsTheSetupTypeInTheExecuteSequenceToo()
    {
        var wxs = XDocument.Load(TestRepository.Path("packaging", "windows", "Package.wxs"));

        var set = Assert.Single(wxs.Descendants(Wix + "SetProperty"), e => (string?)e.Attribute("Id") == "OADM_SETUP");
        Assert.Equal("ClientOnly", (string?)set.Attribute("Value"));
        Assert.Equal("MigrateFeatureStates", (string?)set.Attribute("After"));
        // "both": the UI sequence (setup screens) and the execute sequence (msiexec /qn and /passive skip the UI one).
        Assert.Equal("both", (string?)set.Attribute("Sequence"));
    }

    [Fact]
    public void TheMacChoiceOfTheServerIsTheOneTheHelperLeavesOut()
    {
        var script = File.ReadAllText(TestRepository.Path("packaging", "macos", "build-pkg.sh"));
        Assert.Contains($"<choice id=\"{Contracts.Updates.InstallerCommands.MacServerChoice}\"", script, StringComparison.Ordinal);
        Assert.Contains(Contracts.Updates.Installation.MacServerDaemon, script, StringComparison.Ordinal);
        Assert.Contains(Contracts.Updates.Installation.MacServerFolder, script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLinuxPackagesAreInstalledWhereTheDetectionLooks()
    {
        var unit = File.ReadAllText(TestRepository.Path("packaging", "linux", "oadm-server.service"));
        Assert.Contains("ExecStart=" + Contracts.Updates.Installation.LinuxServerFolder + "/Oadm.Server", unit, StringComparison.Ordinal);
        var build = File.ReadAllText(TestRepository.Path("packaging", "linux", "build-deb.sh"));
        Assert.Contains("$ROOT" + Contracts.Updates.Installation.LinuxClientFolder, build, StringComparison.Ordinal);
    }
}
