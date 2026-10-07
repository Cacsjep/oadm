namespace Oadm.Plugins.Acap.Tests;

public sealed class EapReaderTests
{
    [Fact]
    public async Task Reads_manifest_json()
    {
        var eap = EapBuilder.FromManifest(EapBuilder.Manifest(appName: "hello", version: "1.2.0", architecture: "aarch64", schema: "1.7.1"));

        var m = await EapReader.ReadAsync(new MemoryStream(eap), CancellationToken.None);

        Assert.Equal("hello", m.AppName);
        Assert.Equal("Hello World", m.FriendlyName);
        Assert.Equal("Hello World", m.DisplayName);
        Assert.Equal("Acme", m.Vendor);
        Assert.Equal("1.2.0", m.Version);
        Assert.Equal("aarch64", m.Architecture);
        Assert.Equal("1.7.1", m.SchemaVersion);
        Assert.Equal("3.0", m.EmbeddedSdkVersion);
        Assert.Equal("never", m.RunMode);
        Assert.False(m.RunsAsRoot);
        Assert.Empty(m.CompatibleOsVersions);
        Assert.Equal("manifest.json", m.Source);
    }

    [Fact]
    public async Task Reads_compatible_os_versions_and_root_user()
    {
        var eap = EapBuilder.FromManifest(EapBuilder.Manifest(schema: "2.0.0", osMin: "12.10", osMax: "99", user: "root"));

        var m = await EapReader.ReadAsync(new MemoryStream(eap), CancellationToken.None);

        var range = Assert.Single(m.CompatibleOsVersions);
        Assert.Equal(new OsVersionRange("12.10", "99"), range);
        Assert.True(m.RunsAsRoot);
    }

    [Fact]
    public async Task Reads_legacy_package_conf()
    {
        var eap = EapBuilder.Build(("package.conf", EapBuilder.PackageConf(appName: "legacy", major: "2", minor: "1", micro: "0", appType: "armv7hf", reqEmbDev: "2.12")));

        var m = await EapReader.ReadAsync(new MemoryStream(eap), CancellationToken.None);

        Assert.Equal("legacy", m.AppName);
        Assert.Equal("2.1.0", m.Version);
        Assert.Equal("armv7hf", m.Architecture);
        Assert.Equal("Old Vendor", m.Vendor);
        Assert.Equal("2.12", m.RequiredEmbeddedDevelopmentVersion);
        Assert.Equal("package.conf", m.Source);
    }

    [Fact]
    public async Task Manifest_wins_and_package_conf_fills_gaps()
    {
        var manifest = EapBuilder.Manifest(appName: "hello", version: "1.0.0", architecture: null);
        var eap = EapBuilder.Build(("manifest.json", manifest), ("package.conf", EapBuilder.PackageConf(appName: "hello", major: "9", appType: "aarch64")));

        var m = await EapReader.ReadAsync(new MemoryStream(eap), CancellationToken.None);

        Assert.Equal("1.0.0", m.Version);
        Assert.Equal("aarch64", m.Architecture);
        Assert.Equal("manifest.json", m.Source);
    }

    [Fact]
    public async Task Ignores_metadata_in_subfolders()
    {
        var eap = EapBuilder.Build(("lib/manifest.json", EapBuilder.Manifest(appName: "evil")), ("manifest.json", EapBuilder.Manifest(appName: "good")));

        var m = await EapReader.ReadAsync(new MemoryStream(eap), CancellationToken.None);

        Assert.Equal("good", m.AppName);
    }

    [Fact]
    public async Task Rejects_a_file_that_is_not_gzip()
    {
        var ex = await Assert.ThrowsAsync<InvalidEapException>(() => EapReader.ReadAsync(new MemoryStream("hello world, not an eap"u8.ToArray()), CancellationToken.None));
        Assert.Contains("gzip", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_a_package_without_metadata()
    {
        var eap = EapBuilder.Build(("README", "nothing"));
        var ex = await Assert.ThrowsAsync<InvalidEapException>(() => EapReader.ReadAsync(new MemoryStream(eap), CancellationToken.None));
        Assert.Contains("neither manifest.json nor package.conf", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{"schemaVersion":"1.7.1"}""")]
    public async Task Rejects_invalid_manifest(string manifest)
    {
        await Assert.ThrowsAsync<InvalidEapException>(() => EapReader.ReadAsync(new MemoryStream(EapBuilder.FromManifest(manifest)), CancellationToken.None));
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("../etc")]
    [InlineData("a&action=remove")]
    public async Task Rejects_unsafe_app_names(string name)
    {
        var eap = EapBuilder.FromManifest(EapBuilder.Manifest(appName: name));
        await Assert.ThrowsAsync<InvalidEapException>(() => EapReader.ReadAsync(new MemoryStream(eap), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_missing_version()
    {
        var manifest = """{"schemaVersion":"1.7.1","acapPackageConf":{"setup":{"appName":"x"}}}""";
        var ex = await Assert.ThrowsAsync<InvalidEapException>(() => EapReader.ReadAsync(new MemoryStream(EapBuilder.FromManifest(manifest)), CancellationToken.None));
        Assert.Contains("version", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reads_from_a_file_path()
    {
        var path = Path.Combine(Path.GetTempPath(), "oadm-acap-" + Guid.NewGuid().ToString("N") + ".eap");
        await File.WriteAllBytesAsync(path, EapBuilder.FromManifest(EapBuilder.Manifest(appName: "fromfile")));
        try
        {
            Assert.Equal("fromfile", (await EapReader.ReadAsync(path, CancellationToken.None)).AppName);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
