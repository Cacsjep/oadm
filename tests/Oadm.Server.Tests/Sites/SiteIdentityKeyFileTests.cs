using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Oadm.Server.Sites;
using Oadm.Server.Tests.Support;

namespace Oadm.Server.Tests.Sites;

/// <summary>
/// On Windows the site identity's key lives in a file of the user's key store while its certificate is loaded; a server
/// that stops must not leave one such file per start behind.
/// </summary>
public sealed class SiteIdentityKeyFileTests
{
    [Fact]
    public async Task AStoppedServerLeavesNoKeyFileOfItsSiteIdentityBehind()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // other systems keep a loaded key in memory only
        }

        var files = new List<string>();
        var host = await TestServerHost.StartAsync();
        await using (host)
        {
            var store = host.Get<SiteIdentityStore>();
            await store.LoadOrCreateAsync("OADM-SRV", CancellationToken.None);
            files.Add(KeyFile(store.Current));
            await store.RotateAsync("OADM-SRV", CancellationToken.None);
            files.Add(KeyFile(store.Current));
            Assert.All(files, f => Assert.True(File.Exists(f), f + " exists while the server runs"));
        }

        Assert.All(files, f => Assert.False(File.Exists(f), f + " is removed when the server stops"));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string KeyFile(X509Certificate2 identity)
    {
        using var key = identity.GetECDsaPrivateKey();
        var name = Assert.IsType<ECDsaCng>(key).Key.UniqueName!;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Crypto", "Keys", name);
    }
}
