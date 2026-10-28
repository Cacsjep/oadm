using System.Text;

using Oadm.Plugins.Acap;
using Oadm.Plugins.Acap.Tests;
using Oadm.Plugins.AppManagement.Client;

namespace Oadm.Plugins.AppManagement.Tests;

/// <summary>Renders Install applications and Install licenses offscreen; OADM_SCREENSHOT_DIR writes PNGs.</summary>
public sealed class DialogScreenshotTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oadm-app-shots-" + Guid.NewGuid().ToString("N"));

    public DialogScreenshotTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A virus scanner may still hold a file it was just written: the temp folder stays.
        }
    }

    [Fact]
    public async Task The_install_and_license_dialogs_render()
    {
        var suffix = Headless.Appearance;
        var arm64 = Path.Combine(_dir, "plate_reader_2_10_3_aarch64.eap");
        var arm32 = Path.Combine(_dir, "plate_reader_2_10_3_armv7hf.eap");
        File.WriteAllBytes(arm64, EapBuilder.FromManifest(EapBuilder.Manifest("platereader", "2.10.3", "aarch64", friendlyName: "Parking Plate Reader", vendor: "Acme Parking")));
        File.WriteAllBytes(arm32, EapBuilder.FromManifest(EapBuilder.Manifest("platereader", "2.10.3", "armv7hf", friendlyName: "Parking Plate Reader", vendor: "Acme Parking")));
        // The lambda returns a value: Dispatch then waits for its awaits (a lambda without one would run as async void).
        var rendered = await Headless.Session.Dispatch(async () =>
        {
            var ctx = new FakePageContext();
            var reader = Samples.App("platereader", "Parking Plate Reader", "2.9.1", license: "Missing", vendor: "Acme Parking") with { ApplicationId = "415090" };
            var devices = new List<DeviceApps>
            {
                Samples.Device("10.0.0.40", reader),
                Samples.Device("10.0.0.41"),
                Samples.Device("10.0.0.42") with { Soc = "Axis Artpec-7", Architecture = "armv7hf", Firmware = "12.6.85" },
                Samples.Device("10.0.0.43", reader with { Version = "3.0.0" }),
                Samples.Device("10.0.0.44") with { Soc = "Ambarella CV25", Architecture = "mips", Model = "M3086-V" },
                Samples.Device("10.0.0.45") with { Firmware = "13.0.12" },
                Samples.Failed("10.0.0.50", "Timeout after 15 s"),
            };

            var install = new InstallAppsViewModel(ctx, devices);
            await install.LoadFilesAsync([arm64, arm32]);
            var installWindow = new InstallAppsWindow { DataContext = install };
            Oadm.Client.App.ApplyCrispText(installWindow);
            installWindow.Show();
            Headless.Pump();
            Assert.Equal(4, install.FitCount);
            Headless.Capture(installWindow, $"install-applications-{suffix}.png");
            installWindow.Close();

            // Before a choice: the devices, Install off.
            var empty = new InstallAppsWindow { DataContext = new InstallAppsViewModel(ctx, devices) };
            Oadm.Client.App.ApplyCrispText(empty);
            empty.Show();
            Headless.Pump();
            Headless.Capture(empty, $"install-applications-empty-{suffix}.png");
            empty.Close();

            var rows = devices.Take(4).SelectMany(d => AppRow.For(d with { Applications = [.. d.Applications, Samples.App("objectanalytics", "AXIS Object Analytics", "1.30.4", bundled: true)] }, Samples.Today).Apps).ToList();
            var licenses = new InstallLicensesViewModel(ctx, rows, () => Samples.Today);
            licenses.ApplyKeys(LicenseKeySet.Read(
            [
                ("10.0.0.40.key", Encoding.UTF8.GetBytes(Key(devices[0].Mac!))),
                ("10.0.0.43.key", Encoding.UTF8.GetBytes(Key(devices[3].Mac!, expires: "2026-03-01"))),
                ("readme.txt", Encoding.UTF8.GetBytes("Keys for the parking cameras")),
            ]), 3);
            var licenseWindow = new InstallLicensesWindow { DataContext = licenses };
            Oadm.Client.App.ApplyCrispText(licenseWindow);
            licenseWindow.Show();
            Headless.Pump();
            Assert.Equal(1, licenses.FitCount);
            Headless.Capture(licenseWindow, $"install-licenses-{suffix}.png");
            licenseWindow.Close();
            return true;
        }, CancellationToken.None);
        Assert.True(rendered);
    }

    private static string Key(string mac, string expires = "2027-09-18") => $"""
        <LicenseKey>
            <FormatVersion>1</FormatVersion>
            <ApplicationID>415090</ApplicationID>
            <ExpirationDate>{expires}</ExpirationDate>
            <DeviceID>{mac}</DeviceID>
            <SignatureKeyID>1</SignatureKeyID>
            <Signature>bm90IGEgcmVhbCBzaWduYXR1cmU=</Signature>
        </LicenseKey>
        """;
}
