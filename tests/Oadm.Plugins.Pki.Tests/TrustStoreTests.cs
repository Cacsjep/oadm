using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Pki.Ca;
using Oadm.Plugins.Pki.TrustStore;
using Oadm.Sdk.Network;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The OS trust store installers with a fake process runner, a fake Windows store and a temporary folder as the Linux
/// file system root. Nothing here touches a real trust store.
/// </summary>
public sealed class TrustStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "oadm-pki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _temp;
    private readonly X509Certificate2 _ca = X509CertificateLoader.LoadCertificate(TestCa.Root("OADM Root CA SERVER01").RawData);
    private readonly FakeRunner _runner = new();

    public TrustStoreTests()
    {
        _temp = Path.Combine(_root, "tmp");
        Directory.CreateDirectory(_temp);
    }

    private string FileName => "oadm-" + CaCertificates.Fingerprint(_ca)[..16].ToLowerInvariant() + ".crt";

    public void Dispose()
    {
        _ca.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Theory]
    [InlineData("usr/local/share/ca-certificates", "update-ca-certificates", new string[0])]
    [InlineData("etc/pki/ca-trust/source/anchors", "update-ca-trust", new[] { "extract" })]
    [InlineData("etc/pki/trust/anchors", "update-ca-certificates", new string[0])]
    public async Task Linux_server_writes_the_file_and_runs_the_update_tool(string folder, string tool, string[] args)
    {
        Directory.CreateDirectory(Path.Combine(_root, folder));
        _runner.Tools[tool] = "/usr/sbin/" + tool;
        var installer = Linux(prompt: false);

        Assert.False(await installer.IsInstalledAsync(_ca, CancellationToken.None));
        var result = await installer.InstallAsync(_ca, CancellationToken.None);

        Assert.Equal(new TrustStoreResult(true), result);
        var file = Path.Combine(_root, folder, FileName);
        Assert.StartsWith("-----BEGIN CERTIFICATE-----", await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        var call = Assert.Single(_runner.Calls);
        Assert.Equal("/usr/sbin/" + tool, call.FileName);
        Assert.Equal(args, call.Arguments);
        Assert.True(await installer.IsInstalledAsync(_ca, CancellationToken.None));

        // Installed: a second install does nothing.
        Assert.True((await installer.InstallAsync(_ca, CancellationToken.None)).Installed);
        Assert.Single(_runner.Calls);

        var removed = await installer.RemoveAsync(_ca, CancellationToken.None);
        Assert.False(removed.Installed);
        Assert.False(File.Exists(file));
        Assert.Equal(2, _runner.Calls.Count);
    }

    [Fact]
    public async Task Linux_without_a_known_tool_or_with_a_failing_tool_or_without_permission()
    {
        Assert.Equal(TrustStoreTexts.NoLinuxTool, (await Linux(prompt: false).InstallAsync(_ca, CancellationToken.None)).Error);
        Assert.Equal("No supported certificate tool found (update-ca-certificates or update-ca-trust)", TrustStoreTexts.NoLinuxTool);

        var folder = Path.Combine(_root, "usr/local/share/ca-certificates");
        Directory.CreateDirectory(folder);
        _runner.Tools["update-ca-certificates"] = "/usr/sbin/update-ca-certificates";
        _runner.Answer = _ => new ProcessResult(1, "\nWARNING: ca-certificates.crt does not exist\nmore\n");
        Assert.Equal("update-ca-certificates failed: WARNING: ca-certificates.crt does not exist", (await Linux(prompt: false).InstallAsync(_ca, CancellationToken.None)).Error);

        // A folder where the file should go: the write fails like a permission problem.
        File.Delete(Path.Combine(folder, FileName));
        Directory.CreateDirectory(Path.Combine(folder, FileName));
        Assert.Equal(TrustStoreTexts.PermissionDenied, (await Linux(prompt: false).InstallAsync(_ca, CancellationToken.None)).Error);
        Assert.Equal("Permission denied: the server must run as administrator / root", TrustStoreTexts.PermissionDenied);
    }

    [Fact]
    public async Task Linux_client_without_root_uses_pkexec_or_shows_the_sudo_command()
    {
        Directory.CreateDirectory(Path.Combine(_root, "etc/pki/ca-trust/source/anchors"));
        _runner.Tools["update-ca-trust"] = "/usr/bin/update-ca-trust";
        var target = Path.Combine(_root, "etc/pki/ca-trust/source/anchors", FileName);

        var noPkexec = await Linux(prompt: true).InstallAsync(_ca, CancellationToken.None);
        Assert.False(noPkexec.Installed);
        Assert.Equal($"Run this command as root: sudo sh -c \"install -m 0644 '{Path.Combine(_temp, FileName)}' '{target}' && /usr/bin/update-ca-trust extract\"", noPkexec.Error);
        Assert.Empty(_runner.Calls);

        _runner.Tools["pkexec"] = "/usr/bin/pkexec";
        Assert.True((await Linux(prompt: true).InstallAsync(_ca, CancellationToken.None)).Installed);
        var call = Assert.Single(_runner.Calls);
        Assert.Equal("/usr/bin/pkexec", call.FileName);
        Assert.Equal(["/bin/sh", "-c", $"install -m 0644 '{Path.Combine(_temp, FileName)}' '{target}' && /usr/bin/update-ca-trust extract"], call.Arguments);
        Assert.False(File.Exists(Path.Combine(_temp, FileName))); // the temporary file is removed

        _runner.Answer = _ => new ProcessResult(126, "Error executing command as another user: Not authorized");
        Assert.Equal(TrustStoreTexts.Cancelled, (await Linux(prompt: true).InstallAsync(_ca, CancellationToken.None)).Error);

        // A root client writes directly like the server.
        _runner.Answer = _ => new ProcessResult(0, string.Empty);
        Assert.True((await Linux(prompt: true, root: true).InstallAsync(_ca, CancellationToken.None)).Installed);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task MacOs_checks_the_system_keychain_and_adds_the_ca_as_trusted_root()
    {
        var sha256 = CaCertificates.Fingerprint(_ca);
        var installed = false;
        _runner.Answer = call => call.Arguments[0] == "find-certificate"
            ? new ProcessResult(0, installed ? $"SHA-256 hash: {sha256}\nSHA-1 hash: {_ca.Thumbprint}\n" : "SHA-256 hash: 00AA\n")
            : new ProcessResult(0, string.Empty);
        var installer = TrustStoreInstallers.Create(HostOs.MacOs, _runner, promptForElevation: false, tempDirectory: _temp);

        Assert.False(await installer.IsInstalledAsync(_ca, CancellationToken.None));
        Assert.True((await installer.InstallAsync(_ca, CancellationToken.None)).Installed);
        var add = _runner.Calls[^1];
        Assert.Equal("/usr/bin/security", add.FileName);
        Assert.Equal(["add-trusted-cert", "-d", "-r", "trustRoot", "-k", "/Library/Keychains/System.keychain", Path.Combine(_temp, FileName)], add.Arguments);
        Assert.Equal(["find-certificate", "-Z", "-a", "/Library/Keychains/System.keychain"], _runner.Calls[0].Arguments);

        installed = true;
        Assert.True(await installer.IsInstalledAsync(_ca, CancellationToken.None));
        await installer.RemoveAsync(_ca, CancellationToken.None);
        Assert.Equal(["delete-certificate", "-Z", _ca.Thumbprint, "/Library/Keychains/System.keychain"], _runner.Calls[^1].Arguments);

        installed = false;
        _runner.Answer = call => call.Arguments[0] == "find-certificate"
            ? new ProcessResult(0, string.Empty)
            : new ProcessResult(1, "SecTrustSettingsSetTrustSettings: The authorization was denied since no user interaction was possible.");
        Assert.Equal(TrustStoreTexts.PermissionDenied, (await installer.InstallAsync(_ca, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task MacOs_client_asks_for_administrator_privileges_through_osascript()
    {
        _runner.Answer = call => call.Arguments[0] == "find-certificate" ? new ProcessResult(0, string.Empty) : new ProcessResult(0, string.Empty);
        var installer = TrustStoreInstallers.Create(HostOs.MacOs, _runner, promptForElevation: true, isElevated: () => false, tempDirectory: _temp);

        Assert.True((await installer.InstallAsync(_ca, CancellationToken.None)).Installed);
        var call = _runner.Calls[^1];
        Assert.Equal("/usr/bin/osascript", call.FileName);
        Assert.Equal("-e", call.Arguments[0]);
        Assert.Equal(
            $"do shell script \"/usr/bin/security 'add-trusted-cert' '-d' '-r' 'trustRoot' '-k' '/Library/Keychains/System.keychain' '{Path.Combine(_temp, FileName)}'\" with administrator privileges",
            call.Arguments[1]);

        _runner.Answer = c => c.Arguments[0] == "find-certificate" ? new ProcessResult(0, string.Empty) : new ProcessResult(1, "execution error: User canceled. (-128)");
        Assert.Equal(TrustStoreTexts.Cancelled, (await installer.InstallAsync(_ca, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Windows_server_adds_to_the_machine_root_store()
    {
        var store = new FakeRootStore();
        var installer = TrustStoreInstallers.Create(HostOs.Windows, _runner, promptForElevation: false, windowsStore: store, tempDirectory: _temp);

        Assert.False(await installer.IsInstalledAsync(_ca, CancellationToken.None));
        Assert.True((await installer.InstallAsync(_ca, CancellationToken.None)).Installed);
        Assert.Contains(_ca.Thumbprint, store.Thumbprints);
        Assert.True(await installer.IsInstalledAsync(_ca, CancellationToken.None));
        Assert.False((await installer.RemoveAsync(_ca, CancellationToken.None)).Installed);
        Assert.Empty(store.Thumbprints);
        Assert.Empty(_runner.Calls);

        store.Denied = true;
        Assert.Equal(TrustStoreTexts.PermissionDenied, (await installer.InstallAsync(_ca, CancellationToken.None)).Error);
    }

    [Fact]
    public async Task Windows_client_without_administrator_rights_runs_certutil_elevated()
    {
        var store = new FakeRootStore();
        _runner.Answer = call =>
        {
            store.Thumbprints.Add(_ca.Thumbprint); // what certutil does
            return new ProcessResult(0, string.Empty);
        };
        var installer = TrustStoreInstallers.Create(HostOs.Windows, _runner, promptForElevation: true, windowsStore: store, isElevated: () => false, tempDirectory: _temp);

        Assert.True((await installer.InstallAsync(_ca, CancellationToken.None)).Installed);
        var call = Assert.Single(_runner.Calls);
        Assert.True(call.Elevated);
        Assert.Equal("certutil.exe", call.FileName);
        Assert.Equal(["-addstore", "Root", Path.Combine(_temp, FileName)], call.Arguments);
        Assert.False(File.Exists(Path.Combine(_temp, FileName)));

        // Same computer as the server: already installed, nothing runs again.
        Assert.True((await installer.InstallAsync(_ca, CancellationToken.None)).Installed);
        Assert.Single(_runner.Calls);

        store.Thumbprints.Clear();
        _runner.Answer = _ => new ProcessResult(-1, "The installation was cancelled.", Cancelled: true);
        Assert.Equal(TrustStoreTexts.Cancelled, (await installer.InstallAsync(_ca, CancellationToken.None)).Error);
        _runner.Answer = _ => new ProcessResult(5, string.Empty);
        Assert.Equal("certutil failed: exit code 5", (await installer.InstallAsync(_ca, CancellationToken.None)).Error);

        // An elevated client adds directly.
        var elevated = TrustStoreInstallers.Create(HostOs.Windows, _runner, promptForElevation: true, windowsStore: store, isElevated: () => true, tempDirectory: _temp);
        Assert.True((await elevated.InstallAsync(_ca, CancellationToken.None)).Installed);
        Assert.Equal(3, _runner.Calls.Count);
    }

    [Fact]
    public async Task Other_systems_are_not_supported()
    {
        var installer = TrustStoreInstallers.Create(HostOs.Other, _runner, promptForElevation: false);
        Assert.False(await installer.IsInstalledAsync(_ca, CancellationToken.None));
        Assert.Equal(TrustStoreTexts.Unsupported, (await installer.InstallAsync(_ca, CancellationToken.None)).Error);
    }

    private ITrustStoreInstaller Linux(bool prompt, bool root = false) =>
        TrustStoreInstallers.Create(HostOs.Linux, _runner, prompt, isElevated: () => root, root: _root, tempDirectory: _temp);
}
