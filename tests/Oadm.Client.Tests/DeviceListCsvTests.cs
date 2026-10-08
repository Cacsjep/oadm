using System.Diagnostics;
using System.Text;

using Google.Protobuf.WellKnownTypes;

using NSubstitute;

using Oadm.Client.Devices;
using Oadm.Client.Devices.Toolbar;
using Oadm.Client.Dialogs;
using Oadm.Client.Infrastructure;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;

namespace Oadm.Client.Tests;

/// <summary>Export devices: the CSV writer and the export flow of the Devices page.</summary>
public sealed class DeviceListCsvTests
{
    private static DeviceRowViewModel Row(Device device) => new(device);

    [Fact]
    public void Header_and_every_grid_column_as_text_with_invariant_formats()
    {
        Device device = TestSupport.Device("1", "ACCC8E5F6071", "10.0.0.48", "AXIS P3265-V");
        device.HostName = "cam-entrance";
        device.Category = DeviceCategory.Camera;
        device.ProductType = "Dome Camera";
        device.CertNotAfter = Timestamp.FromDateTime(new DateTime(2027, 3, 4, 10, 0, 0, DateTimeKind.Utc));
        device.CertTrust = CertificateTrust.SelfSigned;
        device.Tags.AddRange(["Building A", "PTZ"]);

        string csv = DeviceListCsv.Write([Row(device)]);

        string[] lines = csv.Split("\r\n");
        Assert.Equal("MAC address,Status,Address,Tags,Host name,Model,Firmware,Category,Product type,DHCP,HTTPS,Certificate expires,Certificate,IEEE 802.1X", lines[0]);
        Assert.Equal("ACCC8E5F6071,OK,10.0.0.48,Building A; PTZ,cam-entrance,AXIS P3265-V,12.11.77,Camera,Dome Camera,Yes,Enabled,2027-03-04,Self-signed,Disabled", lines[1]);
        Assert.Equal("", lines[2]); // CRLF after the last record
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("=1+2", "'=1+2")]
    [InlineData("+49 1", "'+49 1")]
    [InlineData("-cmd", "'-cmd")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("=HYPERLINK(\"x\",\"y\")", "\"'=HYPERLINK(\"\"x\"\",\"\"y\"\")\"")]
    [InlineData("", "")]
    public void Cells_are_quoted_per_rfc_4180_and_never_start_a_formula(string value, string expected) =>
        Assert.Equal(expected, Csv.Field(value));

    [Fact]
    public void The_file_is_utf8_with_bom_and_reads_back()
    {
        Device device = TestSupport.Device("1", "ACCC8E5F6071", "10.0.0.48", "AXIS Q1656-BLE \"Größe\", =x");
        byte[] bytes = DeviceListCsv.ToBytes([Row(device)]);

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        string text = Encoding.UTF8.GetString(bytes);
        CsvRecord[] records = Csv.Read(text).ToArray();
        Assert.Equal(2, records.Length);
        Assert.Equal("MAC address", records[0].Cells[0]); // the BOM is not part of the first header
        Assert.Equal("AXIS Q1656-BLE \"Größe\", =x", records[1].Cells[5]);
        Assert.DoesNotContain("assword", text, StringComparison.OrdinalIgnoreCase); // the client has no passwords to write
    }

    [Fact]
    public void Five_thousand_devices_are_written_fast()
    {
        List<DeviceRowViewModel> rows = Enumerable.Range(0, 5000)
            .Select(i => Row(TestSupport.Device(i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"ACCC8E{i:X6}", $"10.0.{i / 250}.{(i % 250) + 1}", "AXIS P3265-V")))
            .ToList();
        DeviceListCsv.ToBytes(rows.Take(10)); // warm up

        var watch = Stopwatch.StartNew();
        byte[] bytes = DeviceListCsv.ToBytes(rows);
        watch.Stop();

        Assert.Equal(5001, Csv.Read(Encoding.UTF8.GetString(bytes)).Count());
        Assert.True(watch.ElapsedMilliseconds < 500, $"5,000 devices took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Default_file_name_has_the_date() =>
        Assert.Equal("oadm-devices-2026-10-08.csv", DeviceListCsv.DefaultFileName(new DateTime(2026, 10, 8, 23, 59, 0, DateTimeKind.Local)));

    [Fact]
    public async Task Export_saves_the_selection_or_else_every_device_the_search_shows()
    {
        using var fx = new DevicesFixture();
        fx.SeedDevices(
            TestSupport.Device("1", "ACCC8E000001", "10.0.0.1", "AXIS P3265-V"),
            TestSupport.Device("2", "ACCC8E000002", "10.0.0.2", "AXIS M3106-L"),
            TestSupport.Device("3", "ACCC8E000003", "10.0.0.3", "AXIS P3265-V"));
        byte[]? saved = null;
        fx.Dialogs.SaveFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FileType>(), Arg.Any<byte[]>())
            .Returns(call =>
            {
                saved = call.ArgAt<byte[]>(3);
                return "devices.csv";
            });

        fx.Devices.SearchText = "P3265";
        await fx.Devices.OpenHostPageAsync(HostPages.ExportDevices);
        Assert.Equal(["ACCC8E000001", "ACCC8E000003"], Serials(saved!));
        await fx.Dialogs.Received(1).SaveFileAsync("Export devices", Arg.Is<string>(n => n.StartsWith("oadm-devices-", StringComparison.Ordinal) && n.EndsWith(".csv", StringComparison.Ordinal)), Arg.Is<FileType>(t => t.Extension == "csv"), Arg.Any<byte[]>());

        fx.Devices.SearchText = "";
        fx.Select("3", "2"); // written in grid order, not in click order
        await fx.Devices.OpenHostPageAsync(HostPages.ExportDevices);
        Assert.Equal(["ACCC8E000002", "ACCC8E000003"], Serials(saved!));
        Assert.Equal("Export devices: save the 2 selected devices as a CSV file", ExportToolbarPlugin.Tooltip(2));
        Assert.Equal("Export devices: save every device shown as a CSV file", ExportToolbarPlugin.Tooltip(0));

        // Nothing to export: a message, no file.
        fx.Devices.SelectedDevices.Clear();
        fx.Devices.SearchText = "nothing matches";
        await fx.Devices.OpenHostPageAsync(HostPages.ExportDevices);
        await fx.Dialogs.Received(1).ShowMessageAsync("Export devices", "There are no devices to export.");
        await fx.Dialogs.Received(2).SaveFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FileType>(), Arg.Any<byte[]>());

        static string[] Serials(byte[] csv) => Csv.Read(Encoding.UTF8.GetString(csv)).Skip(1).Select(r => r.Cells[0]).ToArray();
    }

    [Fact]
    public async Task A_failed_save_is_shown_in_a_message()
    {
        using var fx = new DevicesFixture();
        fx.SeedDevices(TestSupport.Device("1", "ACCC8E000001", "10.0.0.1", "AXIS P3265-V"));
        fx.Dialogs.SaveFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<FileType>(), Arg.Any<byte[]>())
            .Returns<string?>(_ => throw new IOException("The disk is full."));

        await fx.Devices.OpenHostPageAsync(HostPages.ExportDevices);

        await fx.Dialogs.Received(1).ShowMessageAsync("Export devices", "The file could not be saved: The disk is full.");
    }
}
