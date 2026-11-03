using System.Text.RegularExpressions;
using System.Xml.Linq;

using Oadm.Server.AddOns;
using Oadm.Tests.Shared;

namespace Oadm.Server.Tests.Updates;

/// <summary>
/// Add-on plugins are never in the installers and have their own release ZIPs (docs/spec/add-ons.md rule 1 and
/// "Release files"; docs/spec/packaging.md "Add-on ZIPs"). The archives themselves are checked in release.yml
/// (packaging/addons/check-addon-zip.sh); these tests keep the scripts, the workflow and the server's catalogue in line.
/// </summary>
public sealed partial class AddOnPackagingTests
{
    private static string Read(params string[] parts) => File.ReadAllText(TestRepository.Path(parts)).ReplaceLineEndings("\n");

    /// <summary>Every plugin project: its plugin id (OadmPluginId, else the folder name) and whether it is an add-on.</summary>
    private static List<(string Project, string Id, bool AddOn)> PluginProjects() =>
        [.. Directory.GetDirectories(TestRepository.Path("plugins"))
            .SelectMany(d => Directory.GetFiles(d, "*.csproj"))
            .Order(StringComparer.Ordinal)
            .Select(path =>
            {
                var properties = XDocument.Load(path).Descendants().ToList();
                var id = properties.FirstOrDefault(e => e.Name.LocalName == "OadmPluginId")?.Value.Trim()
                    ?? Path.GetFileName(Path.GetDirectoryName(path)!);
                var addOn = properties.Any(e => e.Name.LocalName == "OadmAddOn" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
                return (Path.GetFileName(path), id, addOn);
            })];

    [Fact]
    public void EveryAddOnOfTheCatalogIsMarkedInAllItsProjectsAndNoOtherProjectIs()
    {
        var projects = PluginProjects();

        // Server and client part of one plugin agree: half an add-on in the installers would load without its other part.
        foreach (var plugin in projects.GroupBy(p => p.Id, StringComparer.Ordinal))
        {
            Assert.True(plugin.All(p => p.AddOn) || plugin.All(p => !p.AddOn),
                $"{plugin.Key}: <OadmAddOn> differs between {string.Join(", ", plugin.Select(p => p.Project))}");
        }

        var marked = projects.Where(p => p.AddOn).Select(p => p.Id).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        Assert.Equal(AddOnCatalog.Entries.Select(e => e.Id).Order(StringComparer.Ordinal), marked);
    }

    [Fact]
    public void TheScriptsBuildTheZipsForTheCatalogPlatformsUnderTheReleaseFileName()
    {
        var ps1 = Read("manage.ps1");
        var sh = Read("manage.sh");
        var release = Read(".github", "workflows", "release.yml");

        Assert.Equal(AddOnCatalog.RuntimeIds, PowerShellRids().Match(ps1).Groups[1].Value.Split(',').Select(r => r.Trim().Trim('\'')));
        Assert.Equal(AddOnCatalog.RuntimeIds, BashRids().Match(sh).Groups[1].Value.Split(' '));
        Assert.Equal(AddOnCatalog.RuntimeIds, ReleaseRids().Match(release).Groups[1].Value.Split(' '));

        // The file name the server downloads (A1) is the one the scripts write and the release checks.
        Assert.Contains($"\"{AddOnCatalog.ReleaseFileName("$id", "$version", "$rid")}\"", ps1, StringComparison.Ordinal);
        Assert.Contains($"\"$out/{AddOnCatalog.ReleaseFileName("$id", "$(app_version)", "$rid")}\"", sh, StringComparison.Ordinal);
        Assert.Contains($"artifacts/packages/{AddOnCatalog.ReleaseFileName("$id", "$version", "$rid")}", release, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePublishOfTheInstallersAndPortableZipsLeavesAddOnsOut()
    {
        var ps1 = Read("manage.ps1");
        var sh = Read("manage.sh");

        Assert.Contains("foreach ($p in @(Get-PluginProjects | Where-Object { -not (Test-AddOn $_) })) {", Function(ps1, "function Publish-Plugins {"), StringComparison.Ordinal);
        Assert.Contains("if is_addon \"$p\"; then continue; fi", Function(sh, "publish_plugins() {"), StringComparison.Ordinal);
        foreach (var script in new[] { ps1, sh, Read("packaging", "addons", "addon-ids.sh"), Read("packaging", "addons", "check-addon-zip.sh") })
        {
            Assert.Contains("<OadmAddOn>", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryAddOnZipHasItsOwnNoticesAndTheCheckCountsTheServerPartInTheClientPart()
    {
        var ps1 = Function(Read("manage.ps1"), "function Invoke-PackageAddOns {");
        var sh = Function(Read("manage.sh"), "package_addons() {");
        var check = Read("packaging", "addons", "check-addon-zip.sh");

        // Written into the add-on folder before the check and the ZIP (tools/Oadm.Notices --addon).
        Assert.Contains("'--addon', $stage", ps1, StringComparison.Ordinal);
        Assert.Contains("--addon \"$stage\"", sh, StringComparison.Ordinal);
        Assert.True(ps1.IndexOf("'--addon'", StringComparison.Ordinal) < ps1.IndexOf("Test-AddOnFolder", StringComparison.Ordinal));
        Assert.True(sh.IndexOf("--addon", StringComparison.Ordinal) < sh.IndexOf("check_addon_folder", StringComparison.Ordinal));
        Assert.Contains("THIRD-PARTY-NOTICES.txt missing", Function(Read("manage.ps1"), "function Test-AddOnFolder("), StringComparison.Ordinal);
        Assert.Contains("THIRD-PARTY-NOTICES.txt missing", Function(Read("manage.sh"), "check_addon_folder() {"), StringComparison.Ordinal);
        Assert.Contains("grep -qx 'THIRD-PARTY-NOTICES.txt' || fail", check, StringComparison.Ordinal);

        // The client part the server hands out (AddOnFolders.ClientPartPatterns) is what the size check counts.
        Assert.Equal(["*.Client.dll", "*.Client.pdb", "*.Server.dll", "*.Server.pdb"], Oadm.Server.AddOns.AddOnFolders.ClientPartPatterns);
        Assert.Contains(@"name ~ /^[^\/]+\.(Client|Server)\.(dll|pdb)$/ || name ~ /^client\//", check, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseBuildsChecksAndAttachesTheAddOnZips()
    {
        var release = Read(".github", "workflows", "release.yml");

        Assert.Contains("./manage.ps1 package addons --rid ${{ matrix.rid }}", release, StringComparison.Ordinal);
        Assert.Contains("./manage.sh package addons --rid ${{ matrix.rid }}", release, StringComparison.Ordinal);
        Assert.Contains("bash packaging/addons/check-addon-zip.sh", release, StringComparison.Ordinal);
        // Not in the installers' artifact (smoke-windows downloads it), in their own one, and in the release with the others.
        Assert.Contains("!artifacts/packages/oadm-addon-*", release, StringComparison.Ordinal);
        Assert.Contains("name: addons-${{ steps.version.outputs.version }}-${{ matrix.rid }}", release, StringComparison.Ordinal);
        Assert.Contains("pattern: \"{oadm,portable,addons}-*\"", release, StringComparison.Ordinal);
        // No add-on folder in any installer or portable ZIP.
        Assert.Contains("must not be in the MSI", release, StringComparison.Ordinal);
        Assert.Contains("must not be in the .pkg", release, StringComparison.Ordinal);
        Assert.Matches("add-on \\$id must not be in it", release);
        // Every Action stays pinned by its commit hash.
        foreach (Match use in Uses().Matches(release))
        {
            Assert.Matches("^[^@]+@[0-9a-f]{40}$", use.Groups[1].Value);
        }
    }

    /// <summary>The body of the function that starts with <paramref name="start"/>, up to the next blank line.</summary>
    private static string Function(string script, string start)
    {
        var at = script.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, $"{start} not found");
        var end = script.IndexOf("\n\n", at, StringComparison.Ordinal);
        return script[at..(end < 0 ? script.Length : end)];
    }

    [GeneratedRegex(@"^\$AddOnRids = @\(([^)]*)\)", RegexOptions.Multiline)]
    private static partial Regex PowerShellRids();

    [GeneratedRegex("^ADDON_RIDS=\"([^\"]*)\"", RegexOptions.Multiline)]
    private static partial Regex BashRids();

    [GeneratedRegex(@"for rid in ([a-z0-9 -]+); do\n\s+grep -q ""  oadm-addon-")]
    private static partial Regex ReleaseRids();

    [GeneratedRegex(@"uses: (\S+)")]
    private static partial Regex Uses();
}
