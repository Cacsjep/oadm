using System.Security.Cryptography.X509Certificates;

using Oadm.Plugins.Pki.Client;
using Oadm.Plugins.Pki.Tasks;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Install certificates sends each file with its private key and password to its device (docs/spec/pki.md "Delete and
/// install safety"): the dialog says in one line how many of the devices are reached over HTTP and needs one confirmation;
/// the task logs a warning per device it sent a key to over HTTP.
/// </summary>
public sealed class KeysOverHttpTests
{
    private static readonly string[] Addresses = ["10.0.0.48", "10.0.0.49", "10.0.0.50"];

    private static InstallCertificatesViewModel Dialog(params bool[] https)
    {
        using var ca = TestCa.Root("Customer CA");
        var files = Addresses.Take(https.Length).ToDictionary(a => "c:/certs/" + a + ".pfx", a =>
        {
            using var leaf = RenewDeleteInstallTests.LeafWithKey(ca, a);
            return TestCa.Pfx("pw", leaf);
        });
        var devices = https.Select((h, i) => (Oadm.Sdk.Devices.IDeviceInfo)new PkiFakeDevice(Guid.NewGuid(), Addresses[i], "ACCC8E00000" + i) { ConnectsOverHttps = h }).ToList();
        var vm = new InstallCertificatesViewModel(new FakeDialogContext(_ => Task.FromResult<string?>(null)), devices, path => files[path]) { Password = "pw" };
        vm.SetFiles([.. files.Keys]);
        return vm;
    }

    [Fact]
    public void No_device_over_http_shows_nothing_extra()
    {
        var vm = Dialog(true, true, true);

        Assert.All(vm.Files, f => Assert.Equal("Matched", f.Status));
        Assert.False(vm.HasHttpWarning);
        Assert.True(vm.CanInstall);
    }

    [Fact]
    public void Some_devices_over_http_need_one_confirmation()
    {
        var vm = Dialog(true, false, true);

        Assert.Equal("1 of the 3 devices that get a file are reached over HTTP: their private keys cross the network unencrypted.", vm.HttpWarning);
        Assert.False(vm.CanInstall);
        Assert.Equal("Tick Send the keys anyway.", vm.InstallBlockedReason);

        vm.SendKeysOverHttp = true;

        Assert.True(vm.CanInstall);
        Assert.True(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public void All_devices_over_http_say_so_and_ca_only_sends_no_key()
    {
        var vm = Dialog(false, false, false);
        Assert.Equal("3 of the 3 devices that get a file are reached over HTTP: their private keys cross the network unencrypted.", vm.HttpWarning);
        Assert.False(vm.CanInstall);

        Assert.Equal("The device is reached over HTTP: its private key crosses the network unencrypted.", Dialog(false).HttpWarning);

        // CA certificates only: no key leaves OADM.
        using var root = TestCa.Root("Customer CA");
        using var publicRoot = X509CertificateLoader.LoadCertificate(root.RawData);
        var device = new PkiFakeDevice(Guid.NewGuid()) { ConnectsOverHttps = false };
        var caOnly = new InstallCertificatesViewModel(new FakeDialogContext(_ => Task.FromResult<string?>(null)), [device], _ => TestCa.Pfx("pw", publicRoot)) { Password = "pw" };
        caOnly.SelectedPurpose = caOnly.Purposes.Single(p => p.Value == InstallPurpose.CaOnly);
        caOnly.SetFiles(["c:/certs/ca.p12"]);
        Assert.False(caOnly.HasHttpWarning);
        Assert.True(caOnly.CanInstall);
    }

    [Theory]
    [InlineData("http://10.0.0.48/", true)]
    [InlineData("https://10.0.0.48/", false)]
    public async Task The_task_logs_a_warning_when_it_sent_a_key_over_http(string address, bool warned)
    {
        await using var h = await TaskHarness.StartAsync(new FakeCamera { BaseAddress = new Uri(address) });
        using var ca = TestCa.Root("Customer CA");
        using var leaf = RenewDeleteInstallTests.LeafWithKey(ca, "10.0.0.48");
        var payload = PkiJson.Serialize(new InstallPayload { Purpose = InstallPurpose.Https, Password = "secret", Files = [new InstallFile(h.Device.Id, "f1", "cam48.pfx")] });

        Assert.Null(await RenewDeleteInstallTests.RunWithFileAsync(h, payload, TestCa.Pfx("secret", leaf)));

        var warnings = h.Context.LogEntries.Where(e => e.Level == TaskLogLevel.Warning && e.Message.Contains("over HTTP", StringComparison.Ordinal)).ToList();
        Assert.Equal(warned ? 1 : 0, warnings.Count);
        Assert.DoesNotContain(h.Context.LogEntries, e => e.Message.Contains("secret", StringComparison.Ordinal));
    }
}
