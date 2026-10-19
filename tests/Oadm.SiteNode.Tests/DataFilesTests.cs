using Oadm.SiteNode.Config;
using Oadm.SiteNode.Identity;
using Oadm.SiteNode.Tests.Support;

namespace Oadm.SiteNode.Tests;

/// <summary>node.json and the identity: only absent files mean a first start; a file that cannot be read stops the start and is kept.</summary>
public sealed class DataFilesTests
{
    [Fact]
    public void ANodeJsonThatCannotBeReadFailsTheStartAndIsKept()
    {
        using var folder = new TempFolder();
        var id = new NodeStore(folder.Path).Read(c => c.Id);
        var path = Path.Combine(folder.Path, NodeStore.FileName);
        var before = File.ReadAllBytes(path);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => new NodeStore(folder.Path));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(File.Exists(path + ".unreadable"));
        Assert.Equal(id, new NodeStore(folder.Path).Read(c => c.Id));
    }

    [Fact]
    public void AFolderInPlaceOfNodeJsonIsNoFirstStart()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, NodeStore.FileName));

        Assert.ThrowsAny<IOException>(() => new NodeStore(folder.Path));
    }

    [Fact]
    public void ADamagedNodeJsonIsKeptAsideAndTheNodeStartsWithDefaults()
    {
        using var folder = new TempFolder();
        var path = Path.Combine(folder.Path, NodeStore.FileName);
        File.WriteAllText(path, "{ not json");

        var store = new NodeStore(folder.Path);

        Assert.NotEmpty(store.Read(c => c.Id));
        Assert.Equal("{ not json", File.ReadAllText(path + ".unreadable"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void AnIdentityThatCannotBeReadFailsTheStartAndIsKept()
    {
        using var folder = new TempFolder();
        string fingerprint;
        using (var identity = new NodeIdentity(folder.Path, "site-pc"))
            fingerprint = identity.FingerprintHex;
        var path = Path.Combine(folder.Path, NodeIdentity.IdentityFileName);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => new NodeIdentity(folder.Path, "site-pc"));

        using var again = new NodeIdentity(folder.Path, "site-pc");
        Assert.Equal(fingerprint, again.FingerprintHex);
    }

    [Fact]
    public void AnIdentityWhoseKeyDoesNotFitIsReplacedAndKeptAside()
    {
        using var folder = new TempFolder();
        string fingerprint;
        using (var identity = new NodeIdentity(folder.Path, "site-pc"))
            fingerprint = identity.FingerprintHex;
        File.WriteAllBytes(Path.Combine(folder.Path, NodeIdentity.KeyFileName), new byte[32]);

        using var renewed = new NodeIdentity(folder.Path, "site-pc");

        Assert.NotEqual(fingerprint, renewed.FingerprintHex);
        Assert.True(File.Exists(Path.Combine(folder.Path, NodeIdentity.IdentityFileName + ".unreadable")));
        using var again = new NodeIdentity(folder.Path, "site-pc");
        Assert.Equal(renewed.FingerprintHex, again.FingerprintHex);
    }
}
