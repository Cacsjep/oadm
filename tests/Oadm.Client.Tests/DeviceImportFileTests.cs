using System.Diagnostics;
using System.Text;

using NSubstitute;

using Oadm.Client.Devices;
using Oadm.Client.Dialogs;
using Oadm.Client.Discovery;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;

namespace Oadm.Client.Tests;

/// <summary>Import devices: reading the CSV (formats, header variants, line problems, limits).</summary>
public sealed class DeviceImportFileTests
{
    private static DeviceImportFile Parse(string text) => DeviceImportFile.Parse("devices.csv", text);

    [Fact]
    public void A_plain_file_has_one_address_per_line()
    {
        DeviceImportFile file = Parse("10.0.0.48\r\n\r\ncamera7.example.com\nhttps://10.0.0.60:8443\r[fe80::1]\n");

        Assert.Equal(["10.0.0.48", "camera7.example.com", "https://10.0.0.60:8443", "[fe80::1]"], file.Lines.Select(l => l.Address));
        Assert.Equal([1, 3, 4, 5], file.Lines.Select(l => l.Line));
        Assert.All(file.Lines, l => Assert.Null(l.Problem));
        Assert.All(file.Lines, l => Assert.False(l.HasCredentials));
    }

    [Fact]
    public void The_export_reads_back_with_its_address_column()
    {
        Device device = TestSupport.Device("1", "ACCC8E5F6071", "10.0.0.48", "AXIS P3265-V, \"dome\"");
        byte[] csv = DeviceListCsv.ToBytes([new DeviceRowViewModel(device), new DeviceRowViewModel(TestSupport.Device("2", "ACCC8E5F6072", "10.0.0.49", "=cmd"))]);

        DeviceImportFile file = DeviceImportFile.Parse("oadm-devices.csv", csv, isTooLarge: false);

        Assert.Equal(["10.0.0.48", "10.0.0.49"], file.Lines.Select(l => l.Address));
        Assert.Equal([2, 3], file.Lines.Select(l => l.Line)); // line 1 is the header
    }

    [Theory]
    [InlineData("Address,User name,Password")]
    [InlineData("ADDRESS,USERNAME,PASSWORD")]
    [InlineData(" ip address , user , password ")]
    [InlineData("Host name;User;Pass")]
    public void Header_names_are_case_insensitive_with_optional_credentials(string header)
    {
        char separator = header.Contains(';', StringComparison.Ordinal) ? ';' : ',';
        DeviceImportFile file = Parse(header + "\n10.0.0.48" + separator + "admin" + separator + " pa ss,word \n10.0.0.49\n");

        ImportLine first = file.Lines[0];
        Assert.Equal(("10.0.0.48", "admin"), (first.Address, first.UserName));
        Assert.Equal(separator == ';' ? " pa ss,word " : " pa ss", first.Password); // passwords keep their spaces
        Assert.True(first.HasCredentials);
        Assert.False(file.Lines[1].HasCredentials);
        Assert.DoesNotContain("pa ss", first.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Extra_columns_are_ignored_and_a_password_alone_is_for_root()
    {
        DeviceImportFile file = Parse("Site,Password,Address,Notes\nHQ,secret,10.0.0.48,\"lobby, left\"\nHQ,,10.0.0.49,x\n,,,\n");

        Assert.Equal(2, file.Lines.Count); // ",,," of a spreadsheet is skipped
        Assert.Equal(("root", "secret"), (file.Lines[0].UserName, file.Lines[0].Password));
        Assert.Null(file.Lines[1].UserName);
    }

    [Fact]
    public void Problems_stay_with_their_line()
    {
        string longLine = "10.0.0.70," + new string('x', DeviceImportFile.MaxLineLength);
        DeviceImportFile file = Parse(string.Join("\n",
            "Address,Notes",
            "10.0.0.48,a",
            "not an address,b",
            "HTTPS://10.0.0.48/,c",
            "ftp://10.0.0.50,d",
            "10.0.0.51/path,e",
            longLine,
            "'=10.0.0.52,formula guarded",
            ",only notes",
            "10.0.0.53,\"never closed"));

        string?[] problems = file.Lines.Select(l => l.Problem).ToArray();
        Assert.Null(problems[0]);
        Assert.Equal("\"not an address\" is not an IP address or host name.", problems[1]);
        Assert.Equal("Listed before in line 2.", problems[2]);
        Assert.Equal("\"ftp://10.0.0.50\" is not an IP address or host name.", problems[3]);
        Assert.Equal("\"10.0.0.51/path\" is not an IP address or host name.", problems[4]);
        Assert.Equal("Line 7 is longer than 1,024 characters.", problems[5]);
        Assert.Equal("\"=10.0.0.52\" is not an IP address or host name.", problems[6]); // the guard is removed, the formula is no address
        Assert.Equal("The line has no address.", problems[7]);
        Assert.Equal("Line 10 has a quote (\") that is not closed.", problems[8]);
        Assert.Equal(8, file.ProblemCount);
    }

    [Theory]
    [InlineData("", "The file contains no addresses.")]
    [InlineData("\r\n\r\n", "The file contains no addresses.")]
    [InlineData("Address,Password\r\n", "The file contains no addresses.")]
    [InlineData("MAC address,Model\nACCC8E5F6071,P3265-V", "The file has no Address column. Name the column with the IP addresses or host names \"Address\".")]
    [InlineData("PK\u0003\u0004\0\0binary", "The file is not a text file. Choose a CSV file or a text file with one address per line.")]
    public void Unusable_files_are_refused_as_a_whole(string text, string message) =>
        Assert.Equal(message, Assert.Throws<DeviceImportException>(() => Parse(text)).Message);

    [Fact]
    public void Limits_on_size_and_lines()
    {
        DeviceImportException tooLarge = Assert.Throws<DeviceImportException>(() => DeviceImportFile.Parse("big.csv", new byte[10], isTooLarge: true));
        Assert.Equal("The file is larger than 2 MB. Split it into smaller files.", tooLarge.Message);

        string many = string.Join("\n", Enumerable.Range(0, DeviceImportFile.MaxLines + 1).Select(i => $"10.{i / 65536}.{i / 256 % 256}.{i % 256}"));
        Assert.Equal("The file has more than 10,000 addresses. Split it into smaller files.", Assert.Throws<DeviceImportException>(() => Parse(many)).Message);
    }

    [Fact]
    public void Encodings_utf8_with_and_without_bom_utf16_and_latin1()
    {
        const string text = "Address,User name,Password\n10.0.0.48,jörg,pässword\n";
        foreach (byte[] bytes in new[]
        {
            Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray(),
            Encoding.UTF8.GetBytes(text),
            Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(text)).ToArray(),
            Encoding.Latin1.GetBytes(text),
        })
        {
            ImportLine line = Assert.Single(DeviceImportFile.Parse("x.csv", bytes, false).Lines);
            Assert.Equal(("10.0.0.48", "jörg", "pässword"), (line.Address, line.UserName, line.Password));
        }
    }

    [Fact]
    public async Task The_toolbar_import_opens_the_add_page_with_every_line_or_explains_an_unusable_file()
    {
        using var fx = new DevicesFixture();
        AddDevicesViewModel? shown = null;
        fx.Dialogs.ShowAddDevicesAsync(Arg.Do<AddDevicesViewModel>(p => shown = p)).Returns(false);
        fx.Dialogs.OpenFileAsync(Arg.Any<string>(), Arg.Any<FileType>(), Arg.Any<int>())
            .Returns(new PickedFile("site.csv", Encoding.UTF8.GetBytes("Address\n10.0.0.48\n10.0.0.49\nbad address\n"), false));

        await fx.Devices.OpenHostPageAsync(HostPages.AddImport);

        await fx.Dialogs.Received(1).OpenFileAsync("Import devices", Arg.Is<FileType>(t => t.Extension == "csv"), DeviceImportFile.MaxBytes);
        Assert.NotNull(shown);
        Assert.True(shown.IsImportMode);
        Assert.Equal(["10.0.0.48", "10.0.0.49", "bad address"], shown.FilteredRows.Select(r => r.Address));
        Assert.Equal(["Waiting", "Waiting", "Not added"], shown.FilteredRows.Select(r => r.ChipText));

        // An unusable file: one message, no page.
        shown = null;
        fx.Dialogs.OpenFileAsync(Arg.Any<string>(), Arg.Any<FileType>(), Arg.Any<int>())
            .Returns(new PickedFile("big.csv", new byte[16], true));
        await fx.Devices.OpenHostPageAsync(HostPages.AddImport);
        Assert.Null(shown);
        await fx.Dialogs.Received(1).ShowMessageAsync("Import devices", "The file is larger than 2 MB. Split it into smaller files.");

        // Cancelled picker: nothing happens.
        fx.Dialogs.OpenFileAsync(Arg.Any<string>(), Arg.Any<FileType>(), Arg.Any<int>()).Returns((PickedFile?)null);
        await fx.Devices.OpenHostPageAsync(HostPages.AddImport);
        Assert.Null(shown);
    }

    [Fact]
    public void Ten_thousand_lines_are_read_fast()
    {
        string text = "Address,User name,Password\n" + string.Join("\n", Enumerable.Range(0, DeviceImportFile.MaxLines).Select(i => $"10.{i / 65536}.{i / 256 % 256}.{i % 256},root,pass{i}"));
        Parse("10.0.0.1"); // warm up

        var watch = Stopwatch.StartNew();
        DeviceImportFile file = Parse(text);
        watch.Stop();

        Assert.Equal(DeviceImportFile.MaxLines, file.Lines.Count);
        Assert.Equal(0, file.ProblemCount);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"10,000 lines took {watch.ElapsedMilliseconds} ms");
    }
}
