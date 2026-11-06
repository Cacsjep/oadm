using System.Text.Json;

using Oadm.Core.Plugins;
using Oadm.Core.Tasks;
using Oadm.Plugins.VapixCommander.Client;

namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>
/// What a request does to a device is decided from the request, on the server, by <see cref="RequestSafety"/>; a
/// command's own flags can only add to it. Changes need the user's confirmation, dangerous requests a second one, and
/// both travel in the request: the page cannot leave them out.
/// </summary>
public sealed class RequestSafetyTests
{
    private static CommandDefinition Raw(string method, string path, string? body = null, string bodyType = BodyTypes.None, bool writes = false, bool dangerous = false)
    {
        var command = Samples.Parse($$"""
            { "id": "custom.raw", "version": 1, "name": "Raw", "category": "Custom", "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ],
              "writes": false, "fields": [],
              "request": { "method": "{{method}}", "path": "/x" }, "response": { "kind": "text" } }
            """);
        command.Request.Path = path; // as it is written, "\" and "%" included
        command.Writes = writes;
        command.Dangerous = dangerous;
        if (body is not null)
        {
            command.Request.BodyType = bodyType;
            command.Request.Body = bodyType is BodyTypes.Json or BodyTypes.Form ? System.Text.Json.Nodes.JsonNode.Parse(body) : System.Text.Json.Nodes.JsonValue.Create(body);
        }

        return command;
    }

    private static SafetyLevel Sent(CommandDefinition command) => RequestSafety.Of(command, CommandRenderer.Render(command, Samples.Values()));

    [Theory]
    [InlineData("/axis-cgi/param.cgi?action=list&group=Brand")]
    [InlineData("/axis-cgi/param.cgi?action=listdefinitions&listformat=xmlschema")]
    [InlineData("/axis-cgi/pwdgrp.cgi?action=get")]
    [InlineData("/axis-cgi/jpg/image.cgi?resolution=640x480")]
    [InlineData("/axis-cgi/com/ptz.cgi?query=position&camera=1")]
    [InlineData("/axis-cgi/io/port.cgi?checkactive=1")]
    [InlineData("/axis-cgi/disks/list.cgi?diskid=all")]
    [InlineData("/config/rest/time/v1")]
    public void A_get_that_only_reads_is_a_read(string path)
    {
        var command = Raw("GET", path);

        Assert.Equal(SafetyLevel.Read, RequestSafety.Of(command));
        Assert.Equal(SafetyLevel.Read, Sent(command));
    }

    [Theory]
    [InlineData("/axis-cgi/param.cgi?action=update&ImageSource.I0.Sensor.Brightness=50")]
    [InlineData("/axis-cgi/param.cgi?action=add&template=x&group=Event")]
    [InlineData("/axis-cgi/param.cgi?action=remove&group=Event.E0")]
    [InlineData("/axis-cgi/param.cgi?ImageSource.I0.Sensor.Brightness=50")]
    [InlineData("/axis-cgi/applications/control.cgi?action=start&package=x")]
    [InlineData("/axis-cgi/mediaclip.cgi?action=play&clip=1")]
    [InlineData("/axis-cgi/io/port.cgi?action=1%3A%2F")]
    [InlineData("/axis-cgi/com/ptz.cgi?move=home&camera=1")]
    [InlineData("/axis-cgi/virtualinput/activate.cgi?schemaversion=1&port=1")]
    [InlineData("/axis-cgi/geolocation/set.cgi?lat=1&lng=2")]
    [InlineData("/axis-cgi/some/new.cgi?action=apply")]
    public void A_get_on_a_changing_path_is_a_write(string path)
    {
        var command = Raw("GET", path);

        Assert.Equal(SafetyLevel.Write, RequestSafety.Of(command));
        Assert.Equal(SafetyLevel.Write, Sent(command));
    }

    [Theory]
    [InlineData("/axis-cgi/factorydefault.cgi")]
    [InlineData("/axis-cgi/hardfactorydefault.cgi")]
    [InlineData("/axis-cgi/restart.cgi")]
    [InlineData("/axis-cgi/admin/restart.cgi")]
    [InlineData("/axis-cgi/firmwareupgrade.cgi?type=normal")]
    [InlineData("/axis-cgi/Restart.CGI")]
    [InlineData("/axis-cgi/pwdgrp.cgi?action=remove&user=root")]
    [InlineData("/axis-cgi/pwdgrp.cgi?action=update&user=root&pwd=x")]
    [InlineData("/axis-cgi/pwdgrp.cgi")]
    [InlineData("/axis-cgi/applications/control.cgi?action=remove&package=x")]
    [InlineData("/axis-cgi/disks/format.cgi?diskid=SD_DISK&filesystem=ext4")]
    [InlineData("/axis-cgi/param.cgi?action=update&Network.eth0.IPAddress=10.0.0.9")]
    [InlineData("/axis-cgi/param.cgi?action=update&root.Network.RTSP.Port=555")]
    [InlineData("/axis-cgi/param.cgi?action=update&HTTPS.Enabled=no")]
    [InlineData("/axis-cgi/param.cgi?action=update&System.BoaGroupPolicy.admin=http")]
    public void A_get_that_resets_restarts_or_changes_accounts_is_dangerous(string path)
    {
        var command = Raw("GET", path);

        Assert.Equal(SafetyLevel.Dangerous, RequestSafety.Of(command));
        Assert.Equal(SafetyLevel.Dangerous, Sent(command));
    }

    [Theory]
    [InlineData("/axis-cgi/basicdeviceinfo.cgi", """{"apiVersion":"1.0","method":"getAllProperties"}""", SafetyLevel.Read)]
    [InlineData("/axis-cgi/firmwaremanagement.cgi", """{"apiVersion":"1.0","method":"status"}""", SafetyLevel.Read)]
    [InlineData("/axis-cgi/network_settings.cgi", """{"apiVersion":"1.0","method":"getNetworkInfo"}""", SafetyLevel.Read)]
    [InlineData("/axis-cgi/time.cgi", """{"apiVersion":"1.0","method":"setDateTime","params":{}}""", SafetyLevel.Write)]
    [InlineData("/axis-cgi/firmwaremanagement.cgi", """{"apiVersion":"1.0","method":"commit"}""", SafetyLevel.Write)]
    [InlineData("/axis-cgi/time.cgi", """[{"method":"getDateTimeInfo"}]""", SafetyLevel.Write)]
    [InlineData("/axis-cgi/firmwaremanagement.cgi", """{"apiVersion":"1.0","method":"factoryDefault","params":{"factoryDefaultMode":"hard"}}""", SafetyLevel.Dangerous)]
    [InlineData("/axis-cgi/firmwaremanagement.cgi", """{"apiVersion":"1.0","method":"reboot"}""", SafetyLevel.Dangerous)]
    [InlineData("/axis-cgi/network_settings.cgi", """{"apiVersion":"1.0","method":"setIPv4AddressConfiguration","params":{}}""", SafetyLevel.Dangerous)]
    public void A_post_is_what_its_json_method_does(string path, string body, SafetyLevel expected)
    {
        var command = Raw("POST", path, body, BodyTypes.Json);

        Assert.Equal(expected, RequestSafety.Of(command));
        Assert.Equal(expected, Sent(command));
    }

    [Fact]
    public void Other_bodies_and_methods_change_the_device()
    {
        const string soap = "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:a=\"http://www.axis.com/vapix/ws/action1\"><s:Body><a:{0}/></s:Body></s:Envelope>";

        Assert.Equal(SafetyLevel.Read, Sent(Raw("POST", "/vapix/services", string.Format(System.Globalization.CultureInfo.InvariantCulture, soap, "GetActionRules"), BodyTypes.Xml)));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("POST", "/vapix/services", string.Format(System.Globalization.CultureInfo.InvariantCulture, soap, "RemoveActionRule"), BodyTypes.Xml)));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("POST", "/vapix/services", "<not-soap/>", BodyTypes.Xml)));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("POST", "/axis-cgi/time.cgi", """{"method":"getDateTimeInfo"}""", BodyTypes.Text))); // not sent as JSON
        Assert.Equal(SafetyLevel.Dangerous, Sent(Raw("POST", "/axis-cgi/pwdgrp.cgi", """{"action":"add","user":"x","pwd":"Secret1!","grp":"users","sgrp":"admin"}""", BodyTypes.Form)));
        Assert.Equal(SafetyLevel.Dangerous, Sent(Raw("POST", "/axis-cgi/firmwaremanagement.cgi", "factoryDefault", BodyTypes.Text)));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("PUT", "/config/rest/time/v1")));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("DELETE", "/config/rest/time/v1")));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("POST", "/axis-cgi/whatever.cgi")));
    }

    [Fact]
    public void The_flags_of_a_command_only_add()
    {
        Assert.Equal(SafetyLevel.Write, Sent(Raw("GET", "/axis-cgi/param.cgi?action=list", writes: true)));
        Assert.Equal(SafetyLevel.Dangerous, Sent(Raw("GET", "/axis-cgi/param.cgi?action=list", dangerous: true)));
        Assert.Equal(SafetyLevel.Dangerous, Sent(Raw("GET", "/axis-cgi/restart.cgi", writes: true)));
    }

    [Fact]
    public void A_value_still_to_be_filled_in_counts_as_the_worst_case_until_it_is_sent()
    {
        var command = Samples.Parse("""
            { "id": "custom.param", "version": 1, "name": "Set a parameter", "category": "Custom", "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ],
              "writes": false,
              "fields": [ { "name": "name", "label": "Parameter", "type": "string" }, { "name": "action", "label": "Action", "type": "string" } ],
              "request": { "method": "GET", "path": "/axis-cgi/param.cgi", "query": { "action": "{{action}}", "{{name}}": "1" } }, "response": { "kind": "text" } }
            """);

        Assert.Equal(SafetyLevel.Dangerous, RequestSafety.Of(command)); // the page: it could become a network parameter
        Assert.Equal(SafetyLevel.Read, RequestSafety.Of(command, CommandRenderer.Render(command, Samples.Values(("action", "list"), ("name", "group")))));
        Assert.Equal(SafetyLevel.Write, RequestSafety.Of(command, CommandRenderer.Render(command, Samples.Values(("action", "update"), ("name", "Image.I0.Text.String")))));
        Assert.Equal(SafetyLevel.Dangerous, RequestSafety.Of(command, CommandRenderer.Render(command, Samples.Values(("action", "update"), ("name", "Network.Enabled")))));
    }

    [Fact]
    public void The_changing_paths_know_every_bundled_command()
    {
        var library = CommandLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Library"));
        Assert.True(library.Commands.Count >= 100);

        foreach (var command in library.Commands)
        {
            // The rule alone, without the command's flags, finds what the library says: nothing bundled changes its kind.
            var flags = command.Dangerous ? SafetyLevel.Dangerous : command.Writes ? SafetyLevel.Write : SafetyLevel.Read;
            var bare = command.Clone();
            bare.Writes = false;
            bare.Dangerous = false;
            Assert.True(flags == RequestSafety.Of(bare), $"{command.Id}: the library says {flags}, the request {RequestSafety.Of(bare)}");
        }
    }

    // ------------------------------------------------------------ the attacker's side

    /// <summary>Ways to write a destructive request so that it looks like another one.</summary>
    public static TheoryData<string> Disguises()
    {
        var data = new TheoryData<string>();
        foreach (var (file, query) in new[]
                 {
                     ("factorydefault.cgi", string.Empty), ("hardfactorydefault.cgi", string.Empty), ("restart.cgi", string.Empty),
                     ("firmwareupgrade.cgi", "?type=normal"), ("pwdgrp.cgi", "?action=remove&user=root"),
                 })
        {
            var encodedDot = file.Replace(".", "%2E", StringComparison.Ordinal);
            string[] paths =
            [
                $"/axis-cgi/{file}#x{query}", // what follows "#" is never sent
                $"/axis-cgi/{file}{query}#x",
                $"/axis-cgi/{file}#{query}",
                $"/axis-cgi/{file}%23x{query}",
                $"/axis-cgi/{file}%3Fx=1{query}",
                $"/axis-cgi\\{file}{query}",
                $"/axis-cgi%2F{file}{query}",
                $"/axis-cgi%5C{file}{query}",
                $"/axis-cgi/{encodedDot}{query}",
                $"/axis-cgi/{encodedDot.ToLowerInvariant()}{query}",
                $"/axis-cgi/{file.Replace(".", "%252E", StringComparison.Ordinal)}{query}",
                $"/axis-cgi/x/../{file}{query}",
                $"/local/../axis-cgi/{file}{query}",
                $"/axis-cgi/x/%2e%2e/{file}{query}",
                $"/axis-cgi/x/.%2E/{file}{query}",
                $"/axis-cgi/jpg/image.cgi/../../{file}{query}",
                $"/axis-cgi/./{file}{query}",
                $"/axis-cgi//{file}{query}",
                $"/axis-cgi/{file.ToUpperInvariant()}{query}",
                $"/AXIS-CGI/{char.ToUpperInvariant(file[0])}{file[1..]}{query}",
                $"/axis-cgi/{file}.{query}",
                $"/axis-cgi/{file}..{query}",
                $"/axis-cgi/{file}%20{query}",
                $"/axis-cgi/{file};x=1{query}",
                $"/axis-cgi/{file};.jpg{query}",
                $"/axis-cgi/{file}%3B.jpg{query}",
                $"/axis-cgi/{file}%00.jpg{query}",
                $"/axis-cgi/{file}/x.jpg{query}",
                $"/axis-cgi/{file}/list.cgi{query}",
            ];
            foreach (var path in paths)
            {
                data.Add(path);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Disguises))]
    public void A_disguised_reset_restart_upgrade_or_account_change_is_dangerous_or_refused(string path)
    {
        var command = Raw("GET", path);

        // The rule alone sees through it, as a GET and as a POST with a read in its body.
        Assert.Equal(SafetyLevel.Dangerous, RequestSafety.Of(command));
        Assert.Equal(SafetyLevel.Dangerous, RequestSafety.Of(Raw("POST", path, ReadBody, BodyTypes.Json)));

        // And what the validator lets through is dangerous when it is sent.
        if (CommandValidator.IsDevicePath(path))
        {
            Assert.Equal(SafetyLevel.Dangerous, Sent(command));
        }
        else
        {
            Assert.Throws<CommandValidationException>(() => CommandRenderer.Render(command, Samples.Values()));
        }
    }

    [Theory]
    [InlineData("/axis-cgi/factorydefault.cgi#x")]
    [InlineData("/axis-cgi/param.cgi?action=list#&action=update")]
    [InlineData("/axis-cgi/restart.cgi%23x")]
    [InlineData("/axis-cgi/restart.cgi%3Fx")]
    [InlineData("/axis-cgi%2Frestart.cgi")]
    [InlineData("/axis-cgi%5Crestart.cgi")]
    [InlineData("/axis-cgi/restart%2Ecgi")]
    [InlineData("/axis-cgi/restart%2ecgi")]
    [InlineData("/axis-cgi/restart%252Ecgi")]
    [InlineData("/axis-cgi/x/%2e%2e/restart.cgi")]
    [InlineData("/axis-cgi/./restart.cgi")]
    [InlineData("/axis-cgi/restart.cgi.")]
    [InlineData("/axis-cgi/restart.cgi%20")]
    [InlineData("/axis-cgi/restart.cgi;x=1")]
    [InlineData("/axis-cgi/restart.cgi%3Bx")]
    [InlineData("/axis-cgi/restart.cgi%00.jpg")]
    [InlineData("/axis-cgi/restart.cgi%0A")]
    [InlineData("/axis-cgi/restart%c0%aecgi")]
    [InlineData("/axis-cgi/restart.cgi%")]
    [InlineData("/axis-cgi/restart.cgi%2")]
    [InlineData("/axis-cgi/restart.cgi%zz")]
    public void A_path_that_is_not_sent_as_it_is_written_is_refused(string path)
    {
        Assert.False(CommandValidator.IsDevicePath(path));
        Assert.Contains(CommandValidator.PathProblem, CommandValidator.Validate(Raw("GET", path)));
    }

    [Theory]
    [InlineData("restart")]
    [InlineData("RESTART")]
    [InlineData("x/../restart")]
    [InlineData("x%2F..%2Frestart")]
    [InlineData("restart.cgi#")]
    [InlineData("restart.cgi?x=")]
    [InlineData("restart.cgi;")]
    [InlineData("restart.cgi/x")]
    public void A_value_in_the_path_is_sent_escaped_and_judged_as_the_device_could_read_it(string value)
    {
        var command = Samples.Parse("""
            { "id": "custom.cgi", "version": 1, "name": "Call a CGI", "category": "Custom", "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ],
              "writes": false, "fields": [ { "name": "cgi", "label": "CGI", "type": "string" } ],
              "request": { "method": "GET", "path": "/axis-cgi/{{cgi}}.cgi" }, "response": { "kind": "text" } }
            """);

        var rendered = CommandRenderer.Render(command, Samples.Values(("cgi", value)));

        Assert.Equal("axis-cgi/" + Uri.EscapeDataString(value) + ".cgi", rendered.RelativeUri);
        Assert.Equal(SafetyLevel.Dangerous, RequestSafety.Of(command, rendered));
    }

    [Theory]
    [InlineData("/axis-cgi/param.cgi?action=list&group=root.Brand")]
    [InlineData("/axis-cgi/io/port.cgi?action=1%3A%2F")]
    [InlineData("/axis-cgi/Restart.CGI")]
    [InlineData("/axis-cgi/restart.cgi/x.jpg")]
    [InlineData("/config/rest/profiles/v1/Front%20door/state")]
    [InlineData("/local/objectanalytics/control.cgi")]
    public void An_accepted_path_is_sent_as_it_is_written(string path)
    {
        Assert.True(CommandValidator.IsDevicePath(path));
        using var request = CommandRenderer.Render(Raw("GET", path), Samples.Values()).ToHttpRequest();

        var sent = new Uri(new Uri("https://10.0.0.48/"), request.RequestUri!);
        Assert.Equal(path, sent.PathAndQuery);
        Assert.Empty(sent.Fragment);

        // What the rule guards against: the client drops what follows a "#".
        Assert.Equal("/axis-cgi/factorydefault.cgi", new Uri(new Uri("https://10.0.0.48/"), new Uri("axis-cgi/factorydefault.cgi#x", UriKind.Relative)).PathAndQuery);
    }

    [Theory]
    [InlineData("/axis-cgi/pwdgrp.cgi?action=get&action=remove&user=root")]
    [InlineData("/axis-cgi/pwdgrp.cgi?action=get;action=remove&user=root")]
    [InlineData("/axis-cgi/pwdgrp.cgi?Action=remove&user=root")]
    [InlineData("/axis-cgi/pwdgrp.cgi?action=get&%61ction=remove&user=root")]
    [InlineData("/axis-cgi/pwdgrp.cgi?action=%72emove&user=root")]
    [InlineData("/axis-cgi/pwdgrp.cgi?action=get&ACTION=")]
    [InlineData("/axis-cgi/param.cgi?action=list&action=update&root.Network.eth0.IPAddress=10.0.0.9")]
    [InlineData("/axis-cgi/param.cgi?action=list;action=update&HTTPS.Enabled=no")]
    [InlineData("/axis-cgi/param.cgi?action=list&Action=update&%4Eetwork.Enabled=no")]
    public void A_second_action_in_the_query_counts(string path)
    {
        var command = Raw("GET", path);

        Assert.Equal(SafetyLevel.Dangerous, RequestSafety.Of(command));
        Assert.Equal(SafetyLevel.Dangerous, Sent(command));
    }

    [Theory]
    [InlineData("/axis-cgi/opticssetup.cgi?autofocus=perform")]
    [InlineData("/axis-cgi/opticssetup.cgi?rzoom=100")]
    [InlineData("/axis-cgi/opticssetup.cgi?rfocus=-0.1")]
    [InlineData("/axis-cgi/opticssetup.cgi")]
    [InlineData("/axis-cgi/record/continuous/addconfiguration.cgi?diskid=SD_DISK&options=camera%3D1")]
    [InlineData("/axis-cgi/record/continuous/removeconfiguration.cgi?profile=1")]
    [InlineData("/axis-cgi/disks/checkdisk.cgi?schemaversion=1&diskid=SD_DISK")]
    [InlineData("/axis-cgi/unknown.cgi?speed=5")]
    [InlineData("/axis-cgi/unknown.cgi?action=list&speed=5")]
    [InlineData("/axis-cgi/unknown.cgi?action=check")]
    [InlineData("/axis-cgi/io/port.cgi?checkactive=1&speed=5")]
    [InlineData("/axis-cgi/enablething.cgi")]
    [InlineData("/axis-cgi/some/SetThing.cgi?schemaversion=1")]
    [InlineData("/axis-cgi/some/resetcounters.cgi")]
    [InlineData("/axis-cgi/some/uninstall.cgi/list.cgi")]
    [InlineData("/local/someapp/settings.cgi")]
    [InlineData("/local/someapp/api.cgi?cmd=wipe")]
    [InlineData("/config/rest/thing/v1?speed=5")]
    public void A_get_that_is_not_known_to_read_is_a_write(string path)
    {
        var command = Raw("GET", path);

        Assert.Equal(SafetyLevel.Write, RequestSafety.Of(command));
        Assert.Equal(SafetyLevel.Write, Sent(command));
    }

    [Theory]
    [InlineData("/axis-cgi/unknown.cgi")]
    [InlineData("/axis-cgi/unknown.cgi?action=list&group=Brand")]
    [InlineData("/axis-cgi/unknown.cgi?action=get&schemaversion=1&camera=2")]
    [InlineData("/axis-cgi/some/list.cgi?anything=1")]
    [InlineData("/axis-cgi/some/getinfo.cgi?id=1")]
    [InlineData("/axis-cgi/jpg/image.cgi?resolution=640x480&compression=30&camera=2&rotation=180")]
    [InlineData("/axis-cgi/mjpg/video.cgi?fps=5&resolution=1280x720")]
    [InlineData("/axis-cgi/serverreport.cgi?mode=zip_with_image")]
    [InlineData("/axis-cgi/io/port.cgi?check=1,2")]
    [InlineData("/axis-cgi/temperaturecontrol.cgi?device=heater&id=0&action=query")]
    [InlineData("/local/someapp/api.cgi")]
    public void A_plain_get_stays_a_read(string path)
    {
        var command = Raw("GET", path);

        Assert.Equal(SafetyLevel.Read, RequestSafety.Of(command));
        Assert.Equal(SafetyLevel.Read, Sent(command));
    }

    private const string ReadBody = """{"apiVersion":"1.0","method":"getAllProperties"}""";

    [Fact]
    public void A_request_reads_only_when_its_method_its_query_and_its_body_all_read()
    {
        // A POST that reads by its body, with a query that changes.
        Assert.Equal(SafetyLevel.Write, Sent(Raw("POST", "/axis-cgi/io/port.cgi?action=2%3A%2F", ReadBody, BodyTypes.Json)));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("POST", "/axis-cgi/basicdeviceinfo.cgi?speed=5", ReadBody, BodyTypes.Json)));
        Assert.Equal(SafetyLevel.Dangerous, Sent(Raw("POST", "/axis-cgi/param.cgi?action=update&Network.Enabled=no", ReadBody, BodyTypes.Json)));
        Assert.Equal(SafetyLevel.Read, Sent(Raw("POST", "/axis-cgi/basicdeviceinfo.cgi", ReadBody, BodyTypes.Json)));

        // A GET with a body: the device may read the body.
        Assert.Equal(SafetyLevel.Write, Sent(Raw("GET", "/axis-cgi/time.cgi", """{"apiVersion":"1.0","method":"setDateTime","params":{}}""", BodyTypes.Json)));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("GET", "/axis-cgi/time.cgi", ReadBody, BodyTypes.Json)));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("GET", "/axis-cgi/time.cgi", "anything", BodyTypes.Text)));
        Assert.Equal(SafetyLevel.Dangerous, Sent(Raw("GET", "/axis-cgi/firmwaremanagement.cgi", """{"apiVersion":"1.0","method":"factoryDefault"}""", BodyTypes.Json)));
        Assert.Equal(SafetyLevel.Dangerous, Sent(Raw("GET", "/axis-cgi/param.cgi?action=list", """{"action":"update","root.Network.eth0.IPAddress":"10.0.0.9"}""", BodyTypes.Form)));
        Assert.Equal(SafetyLevel.Write, RequestSafety.Of(Raw("GET", "/axis-cgi/time.cgi", ReadBody, BodyTypes.Json))); // the page says the same
    }

    [Theory]
    [InlineData("action=update&root.Network.eth0.IPAddress=10.0.0.9", SafetyLevel.Dangerous)]
    [InlineData("action=update\nHTTPS.Enabled=no", SafetyLevel.Dangerous)]
    [InlineData("action=update&System.Boa%47roupPolicy.admin=http", SafetyLevel.Dangerous)]
    [InlineData("action=update&Image.I0.Text.String=x", SafetyLevel.Write)]
    [InlineData("action=list&group=Brand", SafetyLevel.Write)] // a POST with a text body is never a read
    public void A_text_body_on_param_cgi_is_read_like_a_query(string body, SafetyLevel expected)
    {
        var command = Raw("POST", "/axis-cgi/param.cgi", body, BodyTypes.Text);

        Assert.Equal(expected, RequestSafety.Of(command));
        Assert.Equal(expected, Sent(command));

        // A read in the query does not cover it, and a body that only says it is XML is read the same way.
        Assert.Equal(expected, Sent(Raw("POST", "/axis-cgi/param.cgi?action=list", body, BodyTypes.Text)));
        Assert.Equal(expected, Sent(Raw("POST", "/axis-cgi/param.cgi", body, BodyTypes.Xml)));
    }

    [Fact]
    public void A_body_never_lowers_what_its_path_says()
    {
        const string soap = "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\" xmlns:a=\"http://www.axis.com/vapix/ws/action1\">{0}</s:Envelope>";
        string Soap(string inner) => string.Format(System.Globalization.CultureInfo.InvariantCulture, soap, inner);

        // Accounts without an action in the query: a text body that names a read does not make it one.
        Assert.Equal(SafetyLevel.Dangerous, Sent(Raw("POST", "/axis-cgi/pwdgrp.cgi", "action=get", BodyTypes.Text)));
        Assert.Equal(SafetyLevel.Dangerous, Sent(Raw("POST", "/axis-cgi/restart.cgi", ReadBody, BodyTypes.Json)));

        // One SOAP read is a read; a second operation or a second body is not.
        Assert.Equal(SafetyLevel.Read, Sent(Raw("POST", "/vapix/services", Soap("<s:Body><a:GetActionRules/></s:Body>"), BodyTypes.Xml)));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("POST", "/vapix/services", Soap("<s:Body><a:GetActionRules/><a:RemoveActionRule/></s:Body>"), BodyTypes.Xml)));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("POST", "/vapix/services", Soap("<s:Body><a:GetActionRules/></s:Body><s:Body><a:RemoveActionRule/></s:Body>"), BodyTypes.Xml)));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("POST", "/vapix/services?action=remove", Soap("<s:Body><a:GetActionRules/></s:Body>"), BodyTypes.Xml)));

        // A JSON method named in another case is not a known read.
        Assert.Equal(SafetyLevel.Write, Sent(Raw("POST", "/axis-cgi/time.cgi", """{"method":"GetDateTimeInfo"}""", BodyTypes.Json)));
        Assert.Equal(SafetyLevel.Write, Sent(Raw("POST", "/axis-cgi/time.cgi", """{"Method":"getDateTimeInfo"}""", BodyTypes.Json)));
    }

    // ------------------------------------------------------------ the server

    private static async Task<(VapixCommanderPlugin Plugin, FakeCoreContext Ctx, FakeDevice Device)> StartAsync()
    {
        var plugin = new VapixCommanderPlugin(Samples.Directory);
        var ctx = new FakeCoreContext();
        var device = new FakeDevice();
        ctx.DeviceList.All.Add(device);
        await plugin.StartAsync(ctx, CancellationToken.None);
        return (plugin, ctx, device);
    }

    private static async Task<T> Invoke<T>(VapixCommanderPlugin plugin, string method, object request)
        where T : class, new() =>
        CommandJson.Read<T>(await plugin.InvokeAsync(method, CommandJson.Write(request), CancellationToken.None));

    /// <summary>A saved "GET /axis-cgi/factorydefault.cgi" that says it only reads.</summary>
    private static async Task<CommandRef> SaveFactoryDefaultAsReadAsync(VapixCommanderPlugin plugin)
    {
        var command = Raw("GET", "/axis-cgi/factorydefault.cgi");
        command.Id = string.Empty;
        command.Name = "Read something";
        var saved = await Invoke<SaveCommandReply>(plugin, CommanderMethods.Save, new SaveCommandRequest { Command = command });
        Assert.Null(saved.Error);
        Assert.False(saved.Saved!.Command.Writes);
        return new CommandRef { Source = CommandSources.Saved, Id = saved.Saved.Command.Id };
    }

    [Fact]
    public async Task A_saved_factory_default_marked_as_a_read_is_dangerous_for_the_server()
    {
        var (plugin, ctx, device) = await StartAsync();
        var vapix = ctx.VapixFactory.For(device.Id);
        var request = new TryCommandRequest { DeviceId = device.Id, Command = await SaveFactoryDefaultAsReadAsync(plugin) };

        var unconfirmed = await Invoke<TryCommandReply>(plugin, CommanderMethods.TryRequest, request);
        Assert.True(unconfirmed.NeedsConfirmation);
        Assert.True(unconfirmed.Dangerous);

        request.Confirmed = true; // an older page: it confirms changes only
        var half = await Invoke<TryCommandReply>(plugin, CommanderMethods.TryRequest, request);
        Assert.True(half.NeedsConfirmation);
        Assert.True(half.Dangerous);
        Assert.Empty(vapix.Requests);
        Assert.Equal(0, vapix.ApiListCalls);

        request.ConfirmedDangerous = true;
        var sent = await Invoke<TryCommandReply>(plugin, CommanderMethods.TryRequest, request);
        Assert.True(sent.Outcome!.Success);
        Assert.Equal(1, vapix.ApiListCalls); // a change: the fresh API list first
        Assert.Equal("axis-cgi/factorydefault.cgi", Assert.Single(vapix.Requests).Uri);
    }

    [Fact]
    public async Task A_rollout_of_it_is_refused_without_the_confirmations()
    {
        var (plugin, ctx, device) = await StartAsync();
        var runner = (RecordingRunner)ctx.Tasks;
        var request = new RolloutRequest
        {
            DeviceIds = [device.Id],
            Commands =
            [
                new RolloutCommand { Command = new CommandRef { Source = CommandSources.Library, Id = "common.brand.read" } },
                new RolloutCommand { Command = await SaveFactoryDefaultAsReadAsync(plugin) },
            ],
        };

        var unconfirmed = await Invoke<RolloutReply>(plugin, CommanderMethods.Rollout, request);
        Assert.True(unconfirmed.NeedsConfirmation);
        Assert.True(unconfirmed.Dangerous);
        Assert.Equal(["Read something"], unconfirmed.ConfirmCommands);
        Assert.Equal("These commands can restart devices, reset them or cut them off the network, and were not confirmed. Nothing was changed.", unconfirmed.Error);

        request.Confirmed = true; // an older page: it always said so, and never for the dangerous question
        var half = await Invoke<RolloutReply>(plugin, CommanderMethods.Rollout, request);
        Assert.True(half.Dangerous);
        Assert.Equal(CommanderTexts.RolloutDangerousNotConfirmed, half.Error);
        Assert.Empty(runner.Runs);

        request.ConfirmedDangerous = true;
        var started = await Invoke<RolloutReply>(plugin, CommanderMethods.Rollout, request);
        Assert.Null(started.Error);
        Assert.Single(runner.Runs);
    }

    [Fact]
    public async Task A_rollout_that_changes_devices_is_refused_without_the_confirmation_and_a_read_runs_without_one()
    {
        var (plugin, ctx, device) = await StartAsync();
        var runner = (RecordingRunner)ctx.Tasks;
        var command = Raw("GET", "/axis-cgi/param.cgi?action=update&Image.I0.Text.String=x"); // marked as a read
        var change = new RolloutRequest { DeviceIds = [device.Id], Commands = [new RolloutCommand { Command = new CommandRef { Source = CommandSources.Inline, Command = command } }] };

        var refused = await Invoke<RolloutReply>(plugin, CommanderMethods.Rollout, change);
        Assert.True(refused.NeedsConfirmation);
        Assert.False(refused.Dangerous);
        Assert.Equal("These commands change the devices and were not confirmed. Nothing was changed.", refused.Error);
        Assert.Empty(runner.Runs);

        var read = new RolloutRequest { DeviceIds = [device.Id], Commands = [new RolloutCommand { Command = new CommandRef { Source = CommandSources.Library, Id = "common.brand.read" } }] };
        var started = await Invoke<RolloutReply>(plugin, CommanderMethods.Rollout, read);
        Assert.Null(started.Error);
        Assert.False(started.NeedsConfirmation);
        Assert.Single(runner.Runs);
    }

    // ------------------------------------------------------------ the task

    [Fact]
    public async Task A_task_started_by_hand_is_refused_without_the_confirmations_in_its_payload()
    {
        var registry = new PluginRegistry();
        var devices = new FakeDevices();
        var vapix = new FakeVapixFactory();
        var plugin = new VapixCommanderPlugin(Samples.Directory);
        await using var engine = new TaskEngine(new InMemoryTaskStore(), registry, devices, vapix, options: new TaskEngineOptions { MaxParallelTasksPerPlugin = 2 });
        await using var host = new CorePluginHost(registry, devices, vapix, engine, new InMemoryPluginSettingsProvider(), secrets: TestSecrets.Create());
        Assert.True(registry.RegisterCorePlugin(plugin, new PluginOrigin(VapixCommanderPlugin.PluginId, "0.1.0", null)));
        await host.StartAllAsync(CancellationToken.None);
        var device = new FakeDevice();
        devices.All.Add(device);

        async Task<TaskRecord> RunAsync(RolloutPayload payload)
        {
            var ids = await engine.RunAsync(RolloutTaskPlugin.PluginId, [device.Id], JsonSerializer.Serialize(payload, CommandJson.Api), "tech@pc", CancellationToken.None);
            await engine.WaitForCompletionAsync(ids[0], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
            return (await engine.GetAsync(ids[0], CancellationToken.None))!;
        }

        RolloutPayload Payload(string path) => new()
        {
            RolloutId = Guid.NewGuid(),
            Commands = [new RolloutPayloadCommand { Command = Raw("GET", path) }], // marked as a read
        };

        var restart = Payload("/axis-cgi/restart.cgi");
        var refused = await RunAsync(restart);
        Assert.Equal(TaskState.Failed, refused.State);
        Assert.Equal(CommanderTexts.RolloutDangerousNotConfirmed, refused.Devices[0].Message);

        restart.Confirmed = true;
        Assert.Equal(CommanderTexts.RolloutDangerousNotConfirmed, (await RunAsync(restart)).Devices[0].Message);

        var change = await RunAsync(Payload("/axis-cgi/param.cgi?action=update&Image.I0.Text.String=x"));
        Assert.Equal(CommanderTexts.RolloutNotConfirmed, change.Devices[0].Message);
        Assert.Empty(vapix.For(device.Id).Requests);
        Assert.Equal(0, vapix.For(device.Id).ApiListCalls);

        // Marked as a read, but it changes the device: the fresh API list is read before it goes.
        restart.ConfirmedDangerous = true;
        restart.RolloutId = Guid.NewGuid();
        Assert.Equal(TaskState.Done, (await RunAsync(restart)).State);
        Assert.Equal(1, vapix.For(device.Id).ApiListCalls);
        Assert.Equal("axis-cgi/restart.cgi", Assert.Single(vapix.For(device.Id).Requests).Uri);
    }

    // ------------------------------------------------------------ the page

    [Fact]
    public async Task The_page_shows_and_asks_what_the_request_does_and_sends_the_answers()
    {
        var page = await PageFixture.CreateAsync();
        await SaveFactoryDefaultAsReadAsync(page.Plugin);
        await page.Vm.LoadAsync(CancellationToken.None);
        var saved = PageFixture.Flatten(page.Vm.LibraryTree[^1]).First(n => n.Item?.Command.Name == "Read something");
        Assert.True(saved.IsDangerous);
        var item = page.Vm.Add(saved.Item!);
        Assert.Equal("Write · dangerous", item.KindText);

        // No to the dangerous question: nothing starts.
        page.Client.ConfirmAnswers.Enqueue(true);
        page.Client.ConfirmAnswers.Enqueue(false);
        await page.Vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(["Run VAPIX commands", "Dangerous commands"], page.Client.Confirmations.Select(c => c.Title));
        Assert.Empty(page.Runner.Runs);

        page.Client.ConfirmAnswers.Enqueue(true);
        page.Client.ConfirmAnswers.Enqueue(true);
        await page.Vm.RunCommand.ExecuteAsync(null);
        var run = Assert.Single(page.Runner.Runs);
        Assert.Contains("\"confirmed\":true", run.Payload!, StringComparison.Ordinal);
        Assert.Contains("\"confirmedDangerous\":true", run.Payload!, StringComparison.Ordinal);

        // One device: one question that says what it can do.
        page.Client.Confirmations.Clear();
        page.Client.ConfirmAnswers.Enqueue(true);
        await page.Vm.TryCommand.ExecuteAsync(null);
        Assert.Equal(("Send command", "\"Read something\" can restart or reset 10.0.0.48 or cut it off the network. Send it now?"), Assert.Single(page.Client.Confirmations));
        Assert.True(page.Vm.TryResult!.Success);
        Assert.Equal("axis-cgi/factorydefault.cgi", Assert.Single(page.Server.VapixFactory.For(page.Camera.Id).Requests).Uri);
    }

    [Fact]
    public async Task The_page_asks_again_when_the_server_sees_more_in_the_filled_in_request()
    {
        var page = await PageFixture.CreateAsync();
        var command = Samples.Parse("""
            { "id": "", "version": 1, "name": "Call a CGI", "category": "Custom", "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ],
              "writes": false, "fields": [ { "name": "cgi", "label": "CGI", "type": "string" } ],
              "request": { "method": "GET", "path": "/axis-cgi/{{cgi}}.cgi" }, "response": { "kind": "text" } }
            """);
        Assert.Null((await page.Plugin.SaveAsync(new SaveCommandRequest { Command = command }, CancellationToken.None)).Error);
        await page.Vm.LoadAsync(CancellationToken.None);
        var item = page.Vm.Add(PageFixture.Flatten(page.Vm.LibraryTree[^1]).First(n => n.Item?.Command.Name == "Call a CGI").Item!);
        Assert.Equal("Write", item.KindText); // the page cannot know more yet
        item.Fields.Single().Text = "restart";

        // The page asks for the change; the server sees a restart and asks the dangerous question. No: nothing starts.
        page.Client.ConfirmAnswers.Enqueue(true);
        page.Client.ConfirmAnswers.Enqueue(false);
        await page.Vm.RunCommand.ExecuteAsync(null);
        Assert.Equal(["Run VAPIX commands", "Dangerous commands"], page.Client.Confirmations.Select(c => c.Title));
        Assert.Contains("• Call a CGI", page.Client.Confirmations[1].Message, StringComparison.Ordinal);
        Assert.Empty(page.Runner.Runs);
        Assert.False(page.Vm.HasStatus);

        page.Client.ConfirmAnswers.Enqueue(true);
        page.Client.ConfirmAnswers.Enqueue(true);
        await page.Vm.RunCommand.ExecuteAsync(null);
        Assert.Single(page.Runner.Runs);

        // The same for one device.
        page.Client.Confirmations.Clear();
        page.Client.ConfirmAnswers.Enqueue(true);
        page.Client.ConfirmAnswers.Enqueue(false);
        await page.Vm.TryCommand.ExecuteAsync(null);
        Assert.Equal(2, page.Client.Confirmations.Count);
        Assert.Contains("can restart or reset 10.0.0.48", page.Client.Confirmations[1].Message, StringComparison.Ordinal);
        Assert.Empty(page.Server.VapixFactory.For(page.Camera.Id).Requests);
    }

    [Fact]
    public void The_request_builder_keeps_the_boxes_ticked_for_a_request_that_changes_the_device()
    {
        var editor = new RawEditorViewModel { Path = "/axis-cgi/factorydefault.cgi" };
        Assert.True(editor.Writes);
        Assert.True(editor.Dangerous);

        editor.Writes = false;
        editor.Dangerous = false;
        Assert.True(editor.Writes);
        Assert.True(editor.Dangerous);
        var (command, _) = editor.Build(forSave: false);
        Assert.True(command.Writes);
        Assert.True(command.Dangerous);

        editor.Path = "/axis-cgi/param.cgi?action=list&group=Brand";
        editor.Dangerous = false;
        editor.Writes = false;
        Assert.False(editor.Writes);
        Assert.False(editor.Dangerous);
    }
}
