using Oadm.Core.Security;
using Oadm.Core.Tests.Persistence;

namespace Oadm.Core.Tests.Security;

public sealed class MasterKeyFileTests : IDisposable
{
    private readonly string _dir = TestDatabase.NewTempDirectory();

    public void Dispose() => TestDatabase.DeleteDirectory(_dir);

    [Fact]
    public void CreatesKeyOnceAndReloadsIt()
    {
        var path = Path.Combine(_dir, "master.key");

        var first = MasterKeyFile.LoadOrCreate(path);
        var second = MasterKeyFile.LoadOrCreate(path);

        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
        Assert.Equal(first, File.ReadAllBytes(path));
    }

    [Fact]
    public void KeyFileIsOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_dir, "master.key");
        MasterKeyFile.LoadOrCreate(path);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void CorruptKeyFileIsNotOverwritten()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "master.key");
        File.WriteAllBytes(path, new byte[7]);

        Assert.Throws<InvalidOperationException>(() => MasterKeyFile.LoadOrCreate(path));
        Assert.Equal(7, new FileInfo(path).Length);
    }

    [Fact]
    public void ProtectorFromKeyFileSurvivesRestart()
    {
        var path = Path.Combine(_dir, "master.key");

        byte[] blob;
        using (var first = CredentialProtector.FromKeyFile(path))
        {
            blob = first.Protect("survives");
        }

        using var second = CredentialProtector.FromKeyFile(path);
        Assert.Equal("survives", second.Unprotect(blob));
    }
}
