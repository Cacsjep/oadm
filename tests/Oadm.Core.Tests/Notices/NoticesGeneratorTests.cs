using Oadm.Notices;

namespace Oadm.Core.Tests.Notices;

/// <summary>The THIRD-PARTY-NOTICES.txt generator of "manage publish" (tools/Oadm.Notices).</summary>
public sealed class NoticesGeneratorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oadm-notices-" + Guid.NewGuid().ToString("N"));

    public NoticesGeneratorTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void DepsFileListsPackagesAndRuntimePacksButNotProjects()
    {
        var path = Path.Combine(_dir, "Oadm.Server.deps.json");
        File.WriteAllText(path, """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0/win-x64" },
              "libraries": {
                "Oadm.Server/1.0.0": { "type": "project" },
                "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.12": { "type": "runtimepack" },
                "Serilog/4.3.0": { "type": "package", "path": "serilog/4.3.0" },
                "PdfSharp/6.2.4.0": { "type": "reference" }
              }
            }
            """);

        var uses = DepsFile.Read(path, "server");

        Assert.Equal(
            [new PackageUse("Microsoft.NETCore.App.Runtime.win-x64", "10.0.12", true, "server"), new PackageUse("Serilog", "4.3.0", false, "server")],
            uses);
    }

    [Theory]
    [InlineData("MIT", new[] { "MIT" })]
    [InlineData("(MIT OR Apache-2.0)", new[] { "MIT", "Apache-2.0" })]
    [InlineData("MIT AND BSD-3-Clause AND MIT", new[] { "MIT", "BSD-3-Clause" })]
    [InlineData("GPL-2.0-only WITH Classpath-exception-2.0", new[] { "GPL-2.0-only" })]
    [InlineData("LGPL-2.1+", new[] { "LGPL-2.1" })]
    [InlineData("", new string[0])]
    public void LicenseExpressionIdentifiers(string expression, string[] expected) =>
        Assert.Equal(expected, LicenseExpressions.Identifiers(expression));

    [Theory]
    [InlineData("mit", "MIT")]
    [InlineData("Apache-2.0", "Apache-2.0")]
    [InlineData("BSD-3-clause", "BSD-3-Clause")]
    [InlineData("BSD-2-Clause", "BSD-2-Clause")]
    [InlineData("LGPL-2.1-or-later", "LGPL-2.1")]
    [InlineData("LGPL-2.1-only", "LGPL-2.1")]
    [InlineData("OFL-1.1", "OFL-1.1")]
    [InlineData("GPL-3.0-only", null)]
    public void LicenseIdsMapToBundledTemplates(string spdx, string? template) =>
        Assert.Equal(template, LicenseExpressions.TemplateId(spdx));

    [Fact]
    public void PackageReaderReadsNuspecAndLicenseFiles()
    {
        var pkg = Path.Combine(_dir, "packages", "sample.lib", "1.2.3");
        Directory.CreateDirectory(Path.Combine(pkg, "legal", "licenses", "zlib"));
        Directory.CreateDirectory(Path.Combine(pkg, "lib"));
        File.WriteAllText(Path.Combine(pkg, "sample.lib.nuspec"), """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>Sample.Lib</id>
                <version>1.2.3</version>
                <authors>Someone</authors>
                <license type="expression">MIT</license>
                <licenseUrl>https://licenses.nuget.org/MIT</licenseUrl>
                <projectUrl>https://example.org/sample</projectUrl>
                <copyright>Copyright (c) 2026 Someone</copyright>
              </metadata>
            </package>
            """);
        File.WriteAllText(Path.Combine(pkg, "LICENSE.txt"), "﻿MIT License\r\n\r\nCopyright (c) 2026 Someone\r\n\r\n");
        File.WriteAllText(Path.Combine(pkg, "THIRD-PARTY-NOTICES.TXT"), "notices");
        File.WriteAllText(Path.Combine(pkg, "legal", "licenses", "zlib", "LICENSE"), "zlib license");
        File.WriteAllBytes(Path.Combine(pkg, "legal", "logo.png"), [0x89, 0x50, 0x00, 0x01]);
        File.WriteAllText(Path.Combine(pkg, "README.md"), "not a license");
        File.WriteAllText(Path.Combine(pkg, "LICENSE.dll"), "not text");

        var info = PackageReader.Read(Path.Combine(_dir, "packages"), "Sample.Lib", "1.2.3");

        Assert.True(info.Found);
        Assert.Equal("MIT", info.LicenseExpression);
        Assert.Equal("Copyright (c) 2026 Someone", info.Copyright);
        Assert.Equal("https://example.org/sample", info.ProjectUrl);
        Assert.Equal(["LICENSE.txt", "THIRD-PARTY-NOTICES.TXT", "legal/licenses/zlib/LICENSE"], info.Files.Select(f => f.Path));
        Assert.Equal("MIT License\n\nCopyright (c) 2026 Someone\n", info.Files[0].Text);
    }

    [Fact]
    public void PackageReaderTakesTheLicenseFileNamedInTheNuspec()
    {
        var pkg = Path.Combine(_dir, "packages", "filelicensed", "2.0.0");
        Directory.CreateDirectory(Path.Combine(pkg, "docs"));
        File.WriteAllText(Path.Combine(pkg, "filelicensed.nuspec"), """
            <package><metadata><id>FileLicensed</id><version>2.0.0</version><license type="file">docs\EULA.txt</license></metadata></package>
            """);
        File.WriteAllText(Path.Combine(pkg, "docs", "EULA.txt"), "Own license text");

        var info = PackageReader.Read(Path.Combine(_dir, "packages"), "FileLicensed", "2.0.0");

        Assert.Null(info.LicenseExpression);
        Assert.Equal("docs/EULA.txt", info.LicenseFile);
        Assert.Equal("Own license text\n", Assert.Single(info.Files).Text);
    }

    [Fact]
    public void MissingPackageIsReportedNotFound()
    {
        var info = PackageReader.Read(_dir, "Nope", "1.0.0");
        Assert.False(info.Found);
    }

    [Fact]
    public void NoticesListEveryPackageOnceWithItsUsersAndTheFullLicenseTexts()
    {
        var templates = Templates();
        var packages = new Dictionary<string, PackageInfo>
        {
            ["Avalonia"] = Info("Avalonia", "12.0.4", "MIT", copyright: "Copyright (c) AvaloniaUI"),
            ["Grpc.Core.Api"] = Info("Grpc.Core.Api", "2.71.0", "Apache-2.0"),
            ["Mixed"] = Info("Mixed", "1.0.0", "(MIT OR BSD-3-Clause)"),
        };
        var uses = new[]
        {
            new PackageUse("Grpc.Core.Api", "2.71.0", false, "client"),
            new PackageUse("Grpc.Core.Api", "2.71.0", false, "server"),
            new PackageUse("Grpc.Core.Api", "2.71.0", false, "plugin oadm.ntp-server"),
            new PackageUse("Avalonia", "12.0.4", false, "client"),
            new PackageUse("Mixed", "1.0.0", false, "plugin oadm.pki"),
        };

        var result = NoticesWriter.Build(Options(), uses, (id, _) => packages[id], templates);

        Assert.Empty(result.Warnings);
        Assert.Equal(3, result.PackageCount);
        var text = result.Text;
        Assert.Contains("OADM 1.2.0 (win-x64)", text, StringComparison.Ordinal);
        Assert.Contains("Bundled FFmpeg notes", text, StringComparison.Ordinal);
        Assert.Contains("Avalonia 12.0.4\n  License: MIT\n  Copyright: Copyright (c) AvaloniaUI\n", text, StringComparison.Ordinal);
        Assert.Contains("Grpc.Core.Api 2.71.0\n  License: Apache-2.0\n  Used by: server, client, plugin oadm.ntp-server\n", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Avalonia 12.0.4", StringComparison.Ordinal) < text.IndexOf("Grpc.Core.Api 2.71.0", StringComparison.Ordinal));

        // Each named license once, with the packages that use it; the always-included ones even when unused.
        Assert.Contains("=== MIT ===\nUsed by: Avalonia 12.0.4, Mixed 1.0.0\n\nMIT TEXT\n", text, StringComparison.Ordinal);
        Assert.Contains("=== BSD-3-Clause ===\nUsed by: Mixed 1.0.0\n", text, StringComparison.Ordinal);
        Assert.Contains("=== LGPL-2.1 ===\n\nLGPL TEXT\n", text, StringComparison.Ordinal);
        Assert.Contains("=== OFL-1.1 ===\n\nOFL TEXT\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BSD-2-Clause", text, StringComparison.Ordinal);
        Assert.Equal(1, Count(text, "=== MIT ==="));
    }

    [Fact]
    public void PackageLicenseFilesAreIncludedOnceAndTemplateCopiesAreSkipped()
    {
        var packages = new Dictionary<string, PackageInfo>
        {
            ["A"] = Info("A", "1.0.0", "MIT", files: [new PackageFile("THIRD-PARTY-NOTICES.TXT", "Shared notices\nline 2\n"), new PackageFile("legal/COPYING.LGPL", "LGPL   TEXT\n")]),
            ["B"] = Info("B", "2.0.0", "MIT", files: [new PackageFile("ThirdPartyNotices.txt", "Shared notices\r\nline 2")]),
        };
        var uses = new[] { new PackageUse("A", "1.0.0", false, "server"), new PackageUse("B", "2.0.0", false, "client") };

        var result = NoticesWriter.Build(Options(), uses, (id, _) => packages[id], Templates());

        Assert.Equal(1, Count(result.Text, "Shared notices"));
        Assert.Contains("=== A 1.0.0: THIRD-PARTY-NOTICES.TXT ===\nSame text in: B 2.0.0: ThirdPartyNotices.txt\n", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("legal/COPYING.LGPL", result.Text, StringComparison.Ordinal); // equals the LGPL template
    }

    [Fact]
    public void UnresolvableLicensesAreWarnings()
    {
        var packages = new Dictionary<string, PackageInfo>
        {
            ["Gpl"] = Info("Gpl", "1.0.0", "GPL-3.0-only"),
            ["UrlOnly"] = Info("UrlOnly", "1.0.0", null, licenseUrl: "https://example.org/license"),
            ["FileOnly"] = Info("FileOnly", "1.0.0", null, files: [new PackageFile("LICENSE", "Custom")]),
        };
        var uses = packages.Keys.Select(id => new PackageUse(id, "1.0.0", false, "server")).Append(new PackageUse("Gone", "9.9.9", false, "client"));

        var result = NoticesWriter.Build(
            Options(),
            uses,
            (id, version) => packages.TryGetValue(id, out var p) ? p : new PackageInfo(id, version, false, null, null, null, null, null, null, []),
            Templates());

        Assert.Contains(result.Warnings, w => w.StartsWith("Gpl 1.0.0: no license text for 'GPL-3.0-only'", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.StartsWith("UrlOnly 1.0.0: no license expression and no license file", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.StartsWith("Gone 9.9.9: package not found", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Warnings, w => w.StartsWith("FileOnly", StringComparison.Ordinal));
        Assert.Contains("FileOnly 1.0.0\n  License: see the license files of the package (section 4)\n", result.Text, StringComparison.Ordinal);
        Assert.Contains("UrlOnly 1.0.0\n  License: https://example.org/license\n", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputIsDeterministic()
    {
        var packages = new Dictionary<string, PackageInfo> { ["Z"] = Info("Z", "1.0.0", "MIT"), ["a"] = Info("a", "1.0.0", "Apache-2.0") };
        var uses = new[] { new PackageUse("Z", "1.0.0", false, "client"), new PackageUse("a", "1.0.0", false, "server") };

        var first = NoticesWriter.Build(Options(), uses, (id, _) => packages[id], Templates()).Text;
        var second = NoticesWriter.Build(Options(), uses.Reverse(), (id, _) => packages[id], Templates()).Text;

        Assert.Equal(first, second);
    }

    [Fact]
    public void BundledLicenseTemplatesOfTheRepositoryAreComplete()
    {
        var repo = Core.Plugins.PluginPaths.FindRepositoryRoot(AppContext.BaseDirectory)!;
        var templates = new LicenseTemplates(Path.Combine(repo, "packaging", "notices", "licenses"));

        foreach (var id in new[] { "MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "OFL-1.1", "LGPL-2.1" })
        {
            Assert.True(templates.Get(id) is { Length: > 500 }, $"template {id} missing or too short");
        }

        Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", templates.Get("LGPL-2.1"), StringComparison.Ordinal);
        Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", templates.Get("OFL-1.1"), StringComparison.Ordinal);
    }

    private static NoticesOptions Options() => new("1.2.0", "win-x64", "Bundled FFmpeg notes", ["Apache-2.0", "LGPL-2.1", "OFL-1.1"]);

    private static LicenseTemplates Templates() => new(new Dictionary<string, string>
    {
        ["MIT"] = "MIT TEXT",
        ["Apache-2.0"] = "APACHE TEXT",
        ["BSD-2-Clause"] = "BSD2 TEXT",
        ["BSD-3-Clause"] = "BSD3 TEXT",
        ["LGPL-2.1"] = "LGPL TEXT",
        ["OFL-1.1"] = "OFL TEXT",
    });

    private static PackageInfo Info(string id, string version, string? expression, string? copyright = null, string? licenseUrl = null, IReadOnlyList<PackageFile>? files = null) =>
        new(id, version, true, expression, null, licenseUrl, copyright, null, null, files?.Select(f => f with { Text = PackageReader.Normalize(f.Text) }).ToList() ?? []);

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
