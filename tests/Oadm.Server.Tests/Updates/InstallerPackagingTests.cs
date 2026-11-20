using System.Xml.Linq;

using Oadm.Tests.Shared;

namespace Oadm.Server.Tests.Updates;

/// <summary>
/// The installers keep the setup type on a silent update (docs/spec/self-update.md rule 10) and put the apps where users
/// find them (docs/spec/packaging.md "Installers"): shortcuts, menu entries, icons, the same content in .deb and .rpm.
/// </summary>
public sealed class InstallerPackagingTests
{
    private static readonly XNamespace Wix = "http://wixtoolset.org/schemas/v4/wxs";

    /// <summary>The sizes of the app icon in the hicolor theme (icon/oadm-app-icon-&lt;size&gt;.png).</summary>
    private static readonly int[] IconSizes = [16, 24, 32, 48, 64, 128, 256, 512];

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

    [Theory]
    [InlineData("Package.wxs", "ClientExe", "OADM")]
    [InlineData("site-node/Package.wxs", "AppExe", "OADM Site Node")]
    public void TheMsiPutsTheAppInTheStartMenuAndOnTheDesktop(string file, string exe, string name)
    {
        var wxs = XDocument.Load(TestRepository.Path(["packaging", "windows", .. file.Split('/')]));

        var shortcuts = Assert.Single(wxs.Descendants(Wix + "File"), e => (string?)e.Attribute("Id") == exe).Elements(Wix + "Shortcut").ToList();
        Assert.Equal(["DesktopFolder", "ProgramMenuFolder"], shortcuts.Select(s => (string?)s.Attribute("Directory")).Order(StringComparer.Ordinal));
        Assert.All(shortcuts, s =>
        {
            Assert.Equal(name, (string?)s.Attribute("Name"));
            Assert.Equal("OadmIcon.ico", (string?)s.Attribute("Icon"));
            // Advertised: part of the exe's component, removed with it.
            Assert.Equal("yes", (string?)s.Attribute("Advertise"));
        });
        var icon = Assert.Single(wxs.Descendants(Wix + "Icon"), e => (string?)e.Attribute("Id") == "OadmIcon.ico");
        Assert.EndsWith(@"\oadm.ico", (string?)icon.Attribute("SourceFile"), StringComparison.Ordinal);
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
        foreach (var script in new[] { "build-deb.sh", "build-rpm.sh" })
        {
            var build = File.ReadAllText(TestRepository.Path("packaging", "linux", script));
            Assert.Contains("$ROOT" + Contracts.Updates.Installation.LinuxServerFolder, build, StringComparison.Ordinal);
            Assert.Contains("$ROOT" + Contracts.Updates.Installation.LinuxClientFolder, build, StringComparison.Ordinal);
        }

        // Only the .rpm marks its folders: self update then installs the next .rpm with dnf.
        var rpm = File.ReadAllText(TestRepository.Path("packaging", "linux", "build-rpm.sh"));
        Assert.Contains($"\"$ROOT/opt/oadm/server/{Contracts.Updates.Installation.RpmMarker}\"", rpm, StringComparison.Ordinal);
        Assert.Contains($"\"$ROOT/opt/oadm/client/{Contracts.Updates.Installation.RpmMarker}\"", rpm, StringComparison.Ordinal);
        Assert.DoesNotContain(Contracts.Updates.Installation.RpmMarker, File.ReadAllText(TestRepository.Path("packaging", "linux", "build-deb.sh")), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRpmFileNamesAreTheOnesSelfUpdateDownloads()
    {
        var common = File.ReadAllText(TestRepository.Path("packaging", "linux", "rpm", "rpm-common.sh"));
        Assert.Contains("%define _rpmfilename %%{NAME}-%%{VERSION}-%%{RELEASE}.%%{ARCH}.rpm", common, StringComparison.Ordinal);
        Assert.Contains("Release: 1", common, StringComparison.Ordinal);
        Assert.Contains("linux-x64) RPM_ARCH=x86_64", common, StringComparison.Ordinal);
        Assert.Contains("linux-arm64) RPM_ARCH=aarch64", common, StringComparison.Ordinal);
        var platform = new Contracts.Updates.UpdatePlatform(Contracts.Updates.UpdateOs.Linux, System.Runtime.InteropServices.Architecture.X64)
        {
            Package = Contracts.Updates.LinuxPackage.Rpm,
        };
        Assert.Equal("oadm-server-4.9.0-1.x86_64.rpm", platform.InstallerName(Contracts.Updates.UpdateApp.Server, new Version(4, 9, 0)));
    }

    /// <summary>Each Linux menu entry: the installed exe, the icon its packages install, the window class of the app.</summary>
    [Theory]
    [InlineData("oadm.desktop", "build-deb.sh", "build-rpm.sh", "/opt/oadm/client/Oadm.Client", "oadm", "src/Oadm.Client/Oadm.Client.csproj")]
    [InlineData("site-node/oadm-site-node.desktop", "build-site-node-deb.sh", "build-site-node-rpm.sh", "/opt/oadm/site-node-app/oadm-site-node-app", "oadm-site-node", "src/Oadm.SiteNode.App/Oadm.SiteNode.App.csproj")]
    public void TheLinuxMenuEntriesStartTheAppWithItsIcon(string desktopFile, string deb, string rpm, string exec, string icon, string project)
    {
        var entry = ReadDesktopEntry(TestRepository.Path(["packaging", "linux", .. desktopFile.Split('/')]));

        Assert.Equal("Application", entry["Type"]);
        Assert.Equal(exec, entry["Exec"]);
        Assert.Equal(icon, entry["Icon"]);
        Assert.Equal("false", entry["Terminal"]);
        Assert.EndsWith(";", entry["Categories"], StringComparison.Ordinal);
        // Avalonia sets the window class to the entry assembly's name (X11PlatformOptions.WmClass): the dock groups the
        // window with its menu entry only when they match.
        var assembly = XDocument.Load(TestRepository.Path(project.Split('/'))).Descendants("AssemblyName").Single().Value;
        Assert.Equal(assembly, entry["StartupWMClass"]);

        var name = Path.GetFileName(desktopFile);
        foreach (var script in new[] { deb, rpm })
        {
            var build = File.ReadAllText(TestRepository.Path("packaging", "linux", script));
            Assert.Contains($"/usr/share/applications/{name}\"", build, StringComparison.Ordinal);
            Assert.Contains(exec.TrimStart('/'), build, StringComparison.Ordinal);
        }

        // The icon in every size, under the entry's icon name, in both kinds of package.
        Assert.All(IconSizes, n => Assert.True(File.Exists(TestRepository.Path("icon", $"oadm-app-icon-{n}.png")), $"icon/oadm-app-icon-{n}.png"));
        Assert.Contains("for n in 16 24 32 48 64 128 256 512; do", File.ReadAllText(TestRepository.Path("packaging", "linux", "rpm", "rpm-common.sh")), StringComparison.Ordinal);
        var debScript = File.ReadAllText(TestRepository.Path("packaging", "linux", deb));
        Assert.Contains($"/apps/{icon}.png\"", debScript, StringComparison.Ordinal);
        Assert.Contains($"rpm_icons \"$ROOT\" {icon} ", File.ReadAllText(TestRepository.Path("packaging", "linux", rpm)), StringComparison.Ordinal);
    }

    /// <summary>The .rpm packages carry the same systemd units and Linux files as the .deb packages.</summary>
    [Theory]
    [InlineData("build-deb.sh", "build-rpm.sh", "oadm-server.service")]
    [InlineData("build-site-node-deb.sh", "build-site-node-rpm.sh", "oadm-site-node.service")]
    public void TheRpmPackagesHoldWhatTheDebPackagesHold(string deb, string rpm, string unit)
    {
        var debScript = File.ReadAllText(TestRepository.Path("packaging", "linux", deb));
        var rpmScript = File.ReadAllText(TestRepository.Path("packaging", "linux", rpm));

        foreach (var path in new[] { $"/usr/lib/systemd/system/{unit}" })
        {
            Assert.Contains(path, debScript, StringComparison.Ordinal);
            Assert.Contains(path, rpmScript, StringComparison.Ordinal);
        }

        // Every /usr/bin link and app folder of the .deb is in the .rpm too.
        foreach (var line in debScript.Split('\n').Where(l => l.StartsWith("ln -s ", StringComparison.Ordinal) || l.StartsWith("cp -R ", StringComparison.Ordinal)))
        {
            Assert.Contains(line.TrimEnd('\r'), rpmScript, StringComparison.Ordinal);
        }

        // The scriptlets keep the data folders like the .deb: never removed.
        foreach (var scriptlet in Directory.GetFiles(TestRepository.Path("packaging", "linux", "rpm"), "*.post*"))
        {
            var text = File.ReadAllText(scriptlet);
            Assert.DoesNotContain("rm -rf /var/lib/", text, StringComparison.Ordinal);
            // rpm expands % in scriptlets: none may hold one.
            Assert.DoesNotContain("%", text, StringComparison.Ordinal);
        }
    }

    private static Dictionary<string, string> ReadDesktopEntry(string path)
    {
        var lines = File.ReadAllLines(path);
        Assert.Equal("[Desktop Entry]", lines[0]);
        return lines.Skip(1).Where(l => l.Contains('=', StringComparison.Ordinal))
            .Select(l => l.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
    }
}
