using System.Text;

using NSubstitute;

using Oadm.Client.Dialogs;
using Oadm.Client.Discovery;
using Oadm.Contracts.V1;
using Oadm.Sdk.Client;

namespace Oadm.Client.Tests;

/// <summary>Import from ADM: the device export of AXIS Device Manager (format 1.1 of ADM 5 and format 2.0).</summary>
public sealed class AdmDeviceExportTests
{
    // The structure of a real export; the passwords are made up.
    private const string Sample = """
        {
        	"encrypted" : false,
        	"fileFormatVersionMayor" : 1,
        	"fileFormatVersionMinor" : 1,
        	"data" :
        	{
        		"serverInformation" : { "name" : "acs", "address" : "localhost", "ipAddress" : "127.0.0.1" },
        		"deviceData" :
        		{
        			"devices" :
        			[
        				{ "id" : 7326, "macAddress" : "B8A44FCC135D", "model" : "AXIS P12 Mk III", "manufacturer" : "Axis",
        				  "firmwareVersion" : "11.9.65", "address" : "10.0.0.12", "apiMode" : 0, "hostName" : "axis-b8a44fcc135d.home",
        				  "httpPort" : 80, "httpsPort" : 443, "securityMode" : 3, "deviceUserName" : "root", "devicePassword" : "pass-one",
        				  "productType" : "Modular Camera" },
        				{ "id" : 6872, "macAddress" : "B8A44F631339", "model" : "AXIS P3265-V", "manufacturer" : "Axis",
        				  "firmwareVersion" : "12.11.77", "address" : "10.0.0.48", "apiMode" : 0, "hostName" : "axis-b8a44f631339.home",
        				  "httpPort" : 80, "httpsPort" : 443, "securityMode" : 2, "deviceUserName" : "root", "devicePassword" : "pass-two",
        				  "productType" : "Dome Camera" },
        				{ "id" : 9000, "macAddress" : "ACCC8E0A1B2C", "address" : "10.0.0.70", "httpPort" : 8080, "httpsPort" : 8443,
        				  "deviceUserName" : "", "devicePassword" : "" },
        				{ "id" : 9001, "macAddress" : "AC:CC:8E:0A:1B:2D", "address" : "10.0.0.71", "httpPort" : 8080, "httpsPort" : 443,
        				  "deviceUserName" : "operator", "devicePassword" : "pass-three" },
        				{ "id" : 9002, "macAddress" : "ACCC8E0A1B2E", "address" : "10.0.0.48" }
        			],
        			"deviceTagRelation" :
        			[
        				{ "id" : 7328, "deviceTag" : 7327, "device" : 6872 },
        				{ "id" : 7329, "deviceTag" : 7330, "device" : 6872 },
        				{ "id" : 7331, "deviceTag" : 7327, "device" : 9001 }
        			],
        			"deviceTag" : [ { "id" : 7327, "name" : "Building A" }, { "id" : 7330, "name" : "PTZ" } ]
        		}
        	}
        }
        """;

    [Fact]
    public void An_export_becomes_one_line_per_device_with_address_login_MAC_address_and_tags()
    {
        var file = AdmDeviceExport.Parse("DEVICES_AT_acs.json", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(Sample)).ToArray(), false);

        Assert.Equal(["10.0.0.12", "10.0.0.48", "https://10.0.0.70:8443", "http://10.0.0.71:8080", "10.0.0.48"], file.Lines.Select(l => l.Address));
        Assert.Equal(("root", "pass-one"), (file.Lines[0].UserName, file.Lines[0].Password));
        Assert.False(file.Lines[2].HasCredentials); // no login: the credential list
        Assert.Equal(("operator", "pass-three"), (file.Lines[3].UserName, file.Lines[3].Password));
        Assert.Equal(["B8A44FCC135D", "B8A44F631339", "ACCC8E0A1B2C", "ACCC8E0A1B2D", "ACCC8E0A1B2E"], file.Lines.Select(l => l.ExpectedSerial));
        Assert.Equal(["Building A", "PTZ"], file.Lines[1].Tags);
        Assert.Equal(["Building A"], file.Lines[3].Tags);
        Assert.Empty(file.Lines[0].Tags);
        Assert.Equal([null, null, null, null, "Listed before in line 2."], file.Lines.Select(l => l.Problem)); // the same checks as a CSV
        Assert.DoesNotContain("pass-one", file.Lines[0].ToString(), StringComparison.Ordinal); // never printed
        Assert.True(file.FromAdm);
        Assert.Equal(["acs"], file.AdmServers);
    }

    [Fact]
    public async Task An_export_of_several_ADM_servers_asks_first_because_one_OADM_server_may_not_reach_them_all()
    {
        const string several = """
            { "encrypted" : false, "fileFormatVersionMayor" : 1, "data" : {
              "serverInformation" : [ { "name" : "site-a", "address" : "10.1.0.5" }, { "name" : "", "address" : "10.2.0.5" } ],
              "deviceData" : { "devices" : [ { "id" : 1, "address" : "10.1.0.20" }, { "id" : 2, "address" : "10.2.0.20" } ] } } }
            """;
        Assert.Equal(["site-a", "10.2.0.5"], AdmDeviceExport.Parse("x.json", several).AdmServers);

        using var fx = new DevicesFixture();
        AddDevicesViewModel? shown = null;
        fx.Dialogs.ShowImportHelpAsync(Arg.Any<ImportHelpViewModel>()).Returns(true);
        fx.Dialogs.ShowAddDevicesAsync(Arg.Do<AddDevicesViewModel>(p => shown = p)).Returns(false);
        fx.Dialogs.OpenFileAsync(Arg.Any<string>(), Arg.Any<FileType>(), Arg.Any<int>())
            .Returns(new PickedFile("x.json", Encoding.UTF8.GetBytes(several), false));

        // Cancel: no page.
        fx.Dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        await fx.Devices.OpenHostPageAsync(HostPages.AddImportAdm);
        Assert.Null(shown);
        await fx.Dialogs.Received(1).ConfirmAsync("Import from ADM",
            "The export comes from 2 AXIS Device Manager servers (site-a, 10.2.0.5). OADM manages its devices from one server: devices only the other servers reach show Not found.",
            "Import");

        // Import: the page with every device.
        fx.Dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        await fx.Devices.OpenHostPageAsync(HostPages.AddImportAdm);
        Assert.Equal(2, shown!.FilteredRows.Count);
    }

    [Theory]
    [InlineData("""{ "encrypted" : true, "fileFormatVersionMayor" : 1, "data" : "AAEC..." }""", AdmDeviceExport.Encrypted)]
    [InlineData("""{ "devices" : [] }""", AdmDeviceExport.NotAnExport)]
    [InlineData("Address,User name\n10.0.0.48,root", AdmDeviceExport.NotAnExport)]
    [InlineData("""[ 1, 2 ]""", AdmDeviceExport.NotAnExport)]
    [InlineData("""{ "fileFormatVersionMayor" : 1, "data" : { "deviceData" : { "devices" : [] } } }""", "The export contains no devices.")]
    public void Files_that_are_no_usable_export_are_refused_with_the_reason(string text, string message)
    {
        var ex = Assert.Throws<DeviceImportException>(() => AdmDeviceExport.Parse("x.json", text));
        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public void Another_format_version_says_so()
    {
        var ex = Assert.Throws<DeviceImportException>(() => AdmDeviceExport.Parse("x.json", """{ "fileFormatVersionMayor" : 3, "data" : { "deviceData" : { "devices" : [] } } }"""));
        Assert.StartsWith("The export has the format version 3, OADM reads versions 1 and 2.", ex.Message, StringComparison.Ordinal);
    }

    // The structure of a real 2.0 export: passwords base64 (made up here: "pass-one", "Säcret!1"), tags per name with their devices.
    private const string SampleV2 = """
        {
        	"encrypted" : false,
        	"fileFormatVersionMayor" : 2,
        	"fileFormatVersionMinor" : 0,
        	"data" :
        	{
        		"serverInformation" : { "name" : "acs", "address" : "localhost", "ipAddress" : "127.0.0.1" },
        		"deviceData" :
        		{
        			"devices" :
        			[
        				{ "id" : 1, "macAddress" : "B8A44FCC135D", "model" : "AXIS P12 Mk III", "manufacturer" : "Axis", "firmwareVersion" : "11.9.65",
        				  "address" : "10.0.0.12", "apiMode" : 0, "httpPort" : 80, "httpsPort" : 443, "securityMode" : 3,
        				  "deviceUserName" : "root", "devicePassword" : "cGFzcy1vbmU=", "productType" : "Modular Camera" },
        				{ "id" : 2, "macAddress" : "B8A44F631339", "model" : "AXIS P3265-V", "manufacturer" : "Axis", "firmwareVersion" : "12.11.77",
        				  "address" : "10.0.0.48", "apiMode" : 0, "httpPort" : 80, "httpsPort" : 443, "securityMode" : 2,
        				  "deviceUserName" : "root", "devicePassword" : "U8OkY3JldCEx", "productType" : "Dome Camera" },
        				{ "id" : 3, "macAddress" : "ACCC8E0A1B2C", "address" : "10.0.0.70", "deviceUserName" : "", "devicePassword" : "" },
        				{ "id" : 4, "macAddress" : "ACCC8E0A1B2D", "address" : "10.0.0.71", "deviceUserName" : "root", "devicePassword" : "not base64!" },
        				{ "id" : 5, "macAddress" : "ACCC8E0A1B2E", "address" : "10.0.0.72", "deviceUserName" : "root", "devicePassword" : "/w==" }
        			],
        			"deviceTags" :
        			[
        				{ "name" : "New tag", "devices" : [ 2 ] },
        				{ "name" : "Building A", "devices" : [ 1, 2, 99 ] },
        				{ "name" : "", "devices" : [ 3 ] }
        			]
        		}
        	}
        }
        """;

    [Fact]
    public void A_format_2_export_decodes_the_base64_passwords_and_reads_the_tags_per_name()
    {
        var file = AdmDeviceExport.Parse("DEVICES_AT_acs_v2.json", Encoding.UTF8.GetBytes(SampleV2), false);

        Assert.Equal(["10.0.0.12", "10.0.0.48", "10.0.0.70", "10.0.0.71", "10.0.0.72"], file.Lines.Select(l => l.Address));
        Assert.Equal(("root", "pass-one"), (file.Lines[0].UserName, file.Lines[0].Password));
        Assert.Equal(("root", "Säcret!1"), (file.Lines[1].UserName, file.Lines[1].Password)); // UTF-8
        Assert.False(file.Lines[2].HasCredentials); // empty: the credential list
        Assert.Equal(["B8A44FCC135D", "B8A44F631339", "ACCC8E0A1B2C", "ACCC8E0A1B2D", "ACCC8E0A1B2E"], file.Lines.Select(l => l.ExpectedSerial));
        Assert.Equal(["Building A"], file.Lines[0].Tags);
        Assert.Equal(["New tag", "Building A"], file.Lines[1].Tags);
        Assert.Empty(file.Lines[2].Tags); // a tag without a name is left out

        // Not base64, or not UTF-8 text: the row says so and the device gets no login.
        Assert.Equal([null, null, null, AdmDeviceExport.UnreadablePassword, AdmDeviceExport.UnreadablePassword], file.Lines.Select(l => l.Problem));
        Assert.False(file.Lines[3].HasCredentials);
        Assert.False(file.Lines[4].HasCredentials);
        Assert.Equal(["acs"], file.AdmServers);
    }

    [Theory]
    [InlineData(1, "cGFzcy1vbmU=", "cGFzcy1vbmU=")] // 1.x: as it is
    [InlineData(2, "cGFzcy1vbmU=", "pass-one")]
    [InlineData(2, "", "")]
    [InlineData(2, "%%%", null)]
    public void Passwords_are_read_by_the_format_version(int version, string value, string? password) =>
        Assert.Equal(password, AdmDeviceExport.Password(value, version));

    [Fact]
    public void A_device_without_an_address_or_with_a_bad_tag_shows_the_problem_in_its_row()
    {
        var file = AdmDeviceExport.Parse("x.json", """
            { "fileFormatVersionMayor" : 1, "data" : { "deviceData" : {
              "devices" : [ { "id" : 1, "macAddress" : "B8A44F000001" }, { "id" : 2, "address" : "10.0.0.9", "macAddress" : "nope" } ],
              "deviceTagRelation" : [ { "deviceTag" : 5, "device" : 2 } ],
              "deviceTag" : [ { "id" : 5, "name" : "A;B" } ] } } }
            """);
        Assert.Equal("The line has no address.", file.Lines[0].Problem);
        Assert.Equal("The tag \"A;B\" contains ;.", file.Lines[1].Problem);
        Assert.Null(file.Lines[1].ExpectedSerial); // not a MAC address: any device
    }

    [Fact]
    public async Task The_toolbar_entry_shows_the_ADM_explanation_and_opens_the_add_page()
    {
        using var fx = new DevicesFixture();
        AddDevicesViewModel? shown = null;
        ImportHelpViewModel? help = null;
        fx.Dialogs.ShowImportHelpAsync(Arg.Do<ImportHelpViewModel>(h => help = h)).Returns(true);
        fx.Dialogs.ShowAddDevicesAsync(Arg.Do<AddDevicesViewModel>(p => shown = p)).Returns(false);
        fx.Dialogs.OpenFileAsync(Arg.Any<string>(), Arg.Any<FileType>(), Arg.Any<int>())
            .Returns(new PickedFile("DEVICES_AT_acs.json", Encoding.UTF8.GetBytes(Sample), false));

        await fx.Devices.OpenHostPageAsync(HostPages.AddImportAdm);

        Assert.Equal("Import from ADM", help!.Title);
        Assert.True(help.HasWarning);
        await fx.Dialogs.Received(1).OpenFileAsync("Import from ADM", Arg.Is<FileType>(t => t.Extension == "json"), DeviceImportFile.MaxBytes);
        Assert.NotNull(shown);
        Assert.True(shown.IsImportMode);
        Assert.Equal(5, shown.FilteredRows.Count);

        // Not an export: one message, no page.
        shown = null;
        fx.Dialogs.OpenFileAsync(Arg.Any<string>(), Arg.Any<FileType>(), Arg.Any<int>())
            .Returns(new PickedFile("devices.csv", Encoding.UTF8.GetBytes("Address\n10.0.0.48\n"), false));
        await fx.Devices.OpenHostPageAsync(HostPages.AddImportAdm);
        Assert.Null(shown);
        await fx.Dialogs.Received(1).ShowMessageAsync("Import from ADM", AdmDeviceExport.NotAnExport);
    }
}
