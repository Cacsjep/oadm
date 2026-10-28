using System.Diagnostics;

using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Acap;
using Oadm.Plugins.AppManagement.Client;

using Xunit.Abstractions;

namespace Oadm.Plugins.AppManagement.Tests;

/// <summary>30,000 devices with ten applications each (300,000 rows): the page stays fast.</summary>
public sealed class ScaleTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Perf")]
    public void Thirty_thousand_devices_with_ten_applications_filter_search_summarize_and_select_all_within_budget()
    {
        var devices = new List<DeviceApps>(30_000);
        for (var i = 0; i < 30_000; i++)
        {
            var apps = Enumerable.Range(0, 10).Select(n => Samples.App(
                "app" + n,
                "Application " + n,
                version: "1." + ((i + n) % 7),
                status: (i + n) % 5 == 0 ? "Stopped" : "Running",
                license: (i + n) % 11 == 0 ? "Missing" : "Valid",
                expires: (i + n) % 13 == 0 ? "2026-10-30" : "2027-09-18",
                bundled: n == 0)).ToArray();
            devices.Add(Samples.Device($"10.{i / 65536}.{i / 256 % 256}.{i % 256}", apps));
        }

        var ctx = new FakePageContext();
        using var vm = new ApplicationsViewModel(ctx, new FakeTimeProvider(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)));

        var watch = Stopwatch.StartNew();
        vm.ApplyRows(devices);
        var load = watch.Elapsed;
        Assert.Equal(270_000, vm.DeviceRows.Count); // app0 is preinstalled

        watch.Restart();
        vm.HideAxisApps = false;
        Assert.Equal(300_000, vm.DeviceRows.Count);
        var unfiltered = watch.Elapsed;

        watch.Restart();
        vm.SelectedStatus = ApplicationsViewModel.StatusChoices.Single(c => c.Value == AppStatusFilter.NotRunning);
        Assert.Equal(60_000, vm.DeviceRows.Count);
        var filter = watch.Elapsed;

        watch.Restart();
        vm.SearchText = "Application 3";
        Assert.Equal(6_000, vm.DeviceRows.Count);
        var search = watch.Elapsed;

        vm.SearchText = string.Empty;
        vm.SelectedStatus = ApplicationsViewModel.StatusChoices[0];
        watch.Restart();
        vm.SetSelectedDevices(vm.Devices);
        Assert.Equal(300_000, vm.SelectedApps.Count);
        vm.SetDeviceSelection(vm.SelectedApps);
        var items = vm.TargetApplications();
        var targets = vm.TargetDevices();
        var selectAll = watch.Elapsed;
        Assert.Equal(30_000, vm.Devices.Count);
        Assert.Equal(10, items.Count);
        Assert.Equal(30_000, targets.Count);

        output.WriteLine($"load {load.TotalMilliseconds:F0} ms, all rows {unfiltered.TotalMilliseconds:F0} ms, filter {filter.TotalMilliseconds:F0} ms, search {search.TotalMilliseconds:F0} ms, select all {selectAll.TotalMilliseconds:F0} ms");
        Assert.True(load < TimeSpan.FromSeconds(12), $"load took {load.TotalMilliseconds:F0} ms");
        Assert.True(unfiltered < TimeSpan.FromSeconds(3), $"all rows took {unfiltered.TotalMilliseconds:F0} ms");
        Assert.True(filter < TimeSpan.FromSeconds(3), $"filter took {filter.TotalMilliseconds:F0} ms");
        Assert.True(search < TimeSpan.FromSeconds(3), $"search took {search.TotalMilliseconds:F0} ms");
        Assert.True(selectAll < TimeSpan.FromSeconds(3), $"select all took {selectAll.TotalMilliseconds:F0} ms");
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Thirty_thousand_devices_are_checked_for_an_install_and_matched_to_license_keys_within_budget()
    {
        var devices = new List<DeviceApps>(30_000);
        var keys = new List<(string, byte[])>(10_000);
        for (var i = 0; i < 30_000; i++)
        {
            var app = Samples.App("hello", "Hello World", i % 3 == 0 ? "1.0.0" : "1.2.0", license: "Missing", vendor: "Acme") with { ApplicationId = "415090" };
            var device = Samples.Device($"10.{i / 65536}.{i / 256 % 256}.{i % 256}", app) with
            {
                Mac = "B8A44F" + i.ToString("X6", System.Globalization.CultureInfo.InvariantCulture),
                Architecture = i % 2 == 0 ? "aarch64" : "armv7hf",
            };
            devices.Add(device);
            if (i % 3 == 0)
            {
                keys.Add(("k" + i, System.Text.Encoding.UTF8.GetBytes($"<LicenseKey><ApplicationID>415090</ApplicationID><ExpirationDate>2027-09-18</ExpirationDate><DeviceID>{device.Mac}</DeviceID><Signature>x</Signature></LicenseKey>")));
            }
        }

        var dir = Path.Combine(Path.GetTempPath(), "oadm-app-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var eap = Path.Combine(dir, "hello.eap");
            await File.WriteAllBytesAsync(eap, Oadm.Plugins.Acap.Tests.EapBuilder.FromManifest(Oadm.Plugins.Acap.Tests.EapBuilder.Manifest()));
            var install = new InstallAppsViewModel(new FakePageContext(), devices);

            var watch = Stopwatch.StartNew();
            await install.LoadFilesAsync([eap]);
            var check = watch.Elapsed;
            Assert.Equal(15_000, install.FitCount); // aarch64 only

            var rows = devices.SelectMany(d => AppRow.For(d, Samples.Today).Apps).ToList();
            var licenses = new InstallLicensesViewModel(new FakePageContext(), rows, () => Samples.Today);
            watch.Restart();
            licenses.ApplyKeys(LicenseKeySet.Read(keys), keys.Count);
            var request = licenses.BuildRequest();
            var match = watch.Elapsed;
            Assert.Equal(10_000, licenses.FitCount);
            Assert.Equal(10_000, Assert.Single(request.Items).LicenseKeys!.Count);

            output.WriteLine($"install check {check.TotalMilliseconds:F0} ms, license match and request {match.TotalMilliseconds:F0} ms");
            Assert.True(check < TimeSpan.FromSeconds(5), $"install check took {check.TotalMilliseconds:F0} ms");
            Assert.True(match < TimeSpan.FromSeconds(5), $"license match took {match.TotalMilliseconds:F0} ms");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
