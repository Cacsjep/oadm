using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;

using Oadm.Plugins.LicensePlates.Cameras;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Testing;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.LicensePlates.Tests;

/// <summary>
/// The fixtures (Fixtures/): answers of the test camera (AXIS P3265-V, AXIS OS 12.11.147, ALPV 3.1.7, read only on 2026-11-27)
/// with the logins and passwords replaced by "fixture-secret-*"; <c>alpv-3.0.13-config.xml</c> is that answer without what
/// 3.1.7 added (<c>night_mode</c> with <c>sensitivity</c>, <c>nocountry_filter</c>, <c>preview_features</c>, the second number
/// of characters at the top), as the spec describes 3.0.13; the event messages follow the spec's fields (the LPV Simulator
/// sends New and Update with <c>carState</c> "tracked" and never Lost).
/// </summary>
internal static class Fixture
{
    public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static string Config317 => Text("alpv-3.1.7-config.xml");

    public static string Config3013 => Text("alpv-3.0.13-config.xml");

    public static string Areas => Text("alpv-getconfig.json");

    public static string Events => Text("alpv-vapix-events.json");

    public static string Apps => Text("applications-alpv-3.1.7.xml");

    /// <summary>An application list with ALPV in another version, state or license.</summary>
    public static string AppsWith(string? version = "3.1.7", string status = "Running", string license = "Valid") => version is null
        ? """<reply result="ok"><application Name="vmd" NiceName="AXIS Video Motion Detection" Version="4.5" Status="Running" License="None"/></reply>"""
        : $"""<reply result="ok"><application Name="fflprapp" ApplicationID="333330" NiceName="AXIS License Plate Verifier" Vendor="Axis Communications" Version="{version}" Status="{status}" License="{license}"/></reply>""";

    /// <summary>One <c>events:notify</c> message of ALPV as the stream sends it.</summary>
    public static string Notify(string kind, string car, string text, double confidence, string direction = "in", string state = "tracked", string? image = null, string roi = "1")
    {
        var data = new JsonObject
        {
            ["text"] = text,
            ["lpConfidence"] = confidence.ToString("0.00", CultureInfo.InvariantCulture),
            ["carID"] = car,
            ["carState"] = state,
            ["carMoveDirection"] = direction,
            ["roiID"] = roi,
            ["country"] = "AUT",
            ["region"] = "W",
            ["view"] = "rear",
            ["vehicleType"] = "car",
            ["vehicleColor"] = "red",
            ["listName"] = "Allow list",
            ["action"] = "open",
        };
        if (image is not null)
        {
            data["lpImage"] = image;
        }

        return new JsonObject
        {
            ["apiVersion"] = "1.0",
            ["method"] = "events:notify",
            ["params"] = new JsonObject
            {
                ["notification"] = new JsonObject
                {
                    ["topic"] = "tnsaxis:CameraApplicationPlatform/ALPV." + kind,
                    ["timestamp"] = 1795000000000L,
                    ["message"] = new JsonObject { ["source"] = new JsonObject(), ["key"] = new JsonObject(), ["data"] = data },
                },
            },
        }.ToJsonString();
    }

    /// <summary>A tiny JPEG (the start and end markers are all the log checks).</summary>
    public static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0xFF, 0xD9];

    public static string JpegBase64 => Convert.ToBase64String(Jpeg);
}

/// <summary>
/// A camera with ALPV as the test camera answers (the fixtures), which applies ALPV's writes to its own state and records
/// them, and the WebSocket event stream: a filter is answered with <c>data: {}</c>, then <see cref="SendAsync(string)"/> pushes
/// events.
/// </summary>
internal sealed class AlpvCamera : FakeCamera, IVapixClient
{
    private readonly Lock _sync = new();
    private WebSocket? _server;

    public AlpvCamera()
    {
        Respond = Answer;
        Parameters["ImageSource.I0.Sensor.Contrast"] = "50";
        Parameters["ImageSource.I0.Sensor.LocalContrast"] = "10";
        Parameters["ImageSource.I0.Sensor.MaxExposureTime"] = "10000";
        Parameters["ImageSource.I0.Sensor.MaxGain"] = "100";
        Parameters["ImageSource.I0.Sensor.ToneMapping"] = "20";
    }

    public string AppsXml { get; set; } = Fixture.Apps;

    public string ConfigXml { get; set; } = Fixture.Config317;

    public string AreasJson { get; set; } = Fixture.Areas;

    public string EventsJson { get; set; } = Fixture.Events;

    /// <summary>Every write: "POST /local/fflprapp/config.cgi" and its body (the image call too).</summary>
    public List<(string Request, string? Body)> AlpvWrites { get; } = [];

    /// <summary>Configure messages the stream received.</summary>
    public List<string> Filters { get; } = [];

    public int Connections { get; private set; }

    public bool IsConnected => _server is { State: WebSocketState.Open };

    /// <summary>Connections fail with this while set.</summary>
    public Exception? Refuse { get; set; }

    /// <summary>The filter is answered with this error while set.</summary>
    public string? RefuseFilter { get; set; }

    /// <summary>The next reads of the application list fail (a server error) this many times.</summary>
    public int FailAppReads { get; set; }

    private HttpResponseMessage? Answer(HttpRequestMessage request, string? body)
    {
        var uri = request.RequestUri!.OriginalString;
        var post = request.Method == HttpMethod.Post;
        lock (_sync)
        {
            switch (uri)
            {
                case "/axis-cgi/applications/list.cgi" when FailAppReads > 0:
                    FailAppReads--;
                    return new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError);
                case "/axis-cgi/applications/list.cgi":
                    return Ok(AppsXml);
                case "/local/fflprapp/config.cgi" when !post:
                    return Ok(ConfigXml);
                case "/local/fflprapp/config.cgi":
                    AlpvWrites.Add(("POST " + uri, body));
                    ConfigXml = Merge(ConfigXml, body!);
                    return Ok("OK");
                case "/local/fflprapp/api.cgi?api=getconfig":
                    return Ok(AreasJson);
                case "/local/fflprapp/config_json.cgi":
                    AlpvWrites.Add(("POST " + uri, body));
                    AreasJson = new JsonObject { ["roi"] = JsonNode.Parse(body!)!["roi"]!.DeepClone() }.ToJsonString();
                    return Ok("{}");
                case "/local/fflprapp/vapix_events.cgi" when !post:
                    return Ok(EventsJson);
                case "/local/fflprapp/vapix_events.cgi":
                    AlpvWrites.Add(("POST " + uri, body));
                    var events = JsonNode.Parse(EventsJson)!["events_list"]!.AsArray();
                    foreach (var change in JsonNode.Parse(body!)!.AsArray())
                    {
                        foreach (var (key, value) in change!.AsObject())
                        {
                            foreach (var item in events)
                            {
                                if (item!.AsObject().ContainsKey(key))
                                {
                                    item[key] = value!.GetValue<string>();
                                }
                            }
                        }
                    }

                    EventsJson = new JsonObject { ["events_list"] = events.DeepClone() }.ToJsonString();
                    return Ok("{}");
                case "/local/fflprapp/api.cgi?api=camera&sensor=setoptimalparams":
                    AlpvWrites.Add(("GET " + uri, null));
                    Parameters["ImageSource.I0.Sensor.Contrast"] = "65";
                    Parameters["ImageSource.I0.Sensor.LocalContrast"] = "65";
                    Parameters["ImageSource.I0.Sensor.MaxExposureTime"] = "2000";
                    Parameters["ImageSource.I0.Sensor.MaxGain"] = "40";
                    ConfigXml = ConfigXml.Replace("<isparamsoptimal>0</isparamsoptimal>", "<isparamsoptimal>1</isparamsoptimal>", StringComparison.Ordinal);
                    return Ok("OK");
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("Not found") };
            }
        }
    }

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };

    /// <summary>ALPV applies the elements it got and keeps the others.</summary>
    private static string Merge(string config, string changes)
    {
        var root = XElement.Parse(config[config.IndexOf("<configuration", StringComparison.Ordinal)..]);
        var update = XElement.Parse(changes[changes.IndexOf("<configuration", StringComparison.Ordinal)..]);
        void Apply(XElement target, XElement source)
        {
            foreach (var child in source.Elements())
            {
                var existing = target.Element(child.Name);
                if (existing is null)
                {
                    target.Add(new XElement(child));
                }
                else if (child.HasElements)
                {
                    Apply(existing, child);
                }
                else
                {
                    existing.Value = child.Value;
                }
            }
        }

        Apply(root, update);
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + root.ToString(SaveOptions.DisableFormatting);
    }

    Task<WebSocket> IVapixClient.ConnectWebSocketAsync(string pathAndQuery, TimeSpan keepAlive, CancellationToken ct)
    {
        Assert.Equal(EventStreamSocket.StreamPath, pathAndQuery);
        if (Refuse is { } refuse)
        {
            throw refuse;
        }

        Connections++;
        var (client, server) = MemoryDuplex.Pair();
        var socket = WebSocket.CreateFromStream(server, new WebSocketCreationOptions { IsServer = true });
        lock (_sync)
        {
            _server = socket;
        }

        _ = Task.Run(() => ServeAsync(socket), CancellationToken.None);
        return Task.FromResult(WebSocket.CreateFromStream(client, new WebSocketCreationOptions { IsServer = false, KeepAliveInterval = Timeout.InfiniteTimeSpan }));
    }

    private async Task ServeAsync(WebSocket socket)
    {
        try
        {
            while (await EventStreamSocket.ReceiveTextAsync(socket, CancellationToken.None) is { } text)
            {
                lock (_sync)
                {
                    Filters.Add(text);
                }

                var context = JsonNode.Parse(text)!["context"]!.GetValue<string>();
                var reply = RefuseFilter is { } error
                    ? $$$"""{"apiVersion":"1.0","context":"{{{context}}}","method":"events:configure","error":{"code":2104,"message":"{{{error}}}"}}"""
                    : $$$"""{"apiVersion":"1.0","context":"{{{context}}}","method":"events:configure","data":{}}""";
                await socket.SendAsync(Encoding.UTF8.GetBytes(reply), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    /// <summary>Sends one message on the open stream.</summary>
    public async Task SendAsync(string message)
    {
        var socket = _server ?? throw new InvalidOperationException("Not connected.");
        await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>Breaks the stream (network gone).</summary>
    public void Drop()
    {
        WebSocket? socket;
        lock (_sync)
        {
            socket = _server;
            _server = null;
        }

        socket?.Abort();
    }
}

/// <summary>Two connected in-memory streams (a WebSocket client and server without a network).</summary>
internal sealed class MemoryDuplex(Stream reader, Stream writer) : Stream
{
    public static (Stream Client, Stream Server) Pair()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        return (new MemoryDuplex(toClient.Reader.AsStream(), toServer.Writer.AsStream()), new MemoryDuplex(toServer.Reader.AsStream(), toClient.Writer.AsStream()));
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => writer.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => writer.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => reader.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => reader.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => writer.Write(buffer, offset, count);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await writer.WriteAsync(buffer, cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            reader.Dispose();
            writer.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>A started plugin over a <see cref="FakeCoreContext"/> with ALPV cameras, a log folder and a fake clock.</summary>
internal sealed class Rig : IAsyncDisposable
{
    private Rig(FakeCoreContext ctx, LicensePlatesPlugin plugin, FakeEventPublisher publisher, Microsoft.Extensions.Time.Testing.FakeTimeProvider time, string folder)
    {
        Ctx = ctx;
        Plugin = plugin;
        Publisher = publisher;
        Time = time;
        Folder = folder;
    }

    public FakeCoreContext Ctx { get; }

    public LicensePlatesPlugin Plugin { get; }

    public FakeEventPublisher Publisher { get; }

    public Microsoft.Extensions.Time.Testing.FakeTimeProvider Time { get; }

    public string Folder { get; }

    public FakeCallContext Admin => Ctx.CallAsAllDevices();

    /// <summary>A rig with <paramref name="cameras"/> ALPV cameras (not added to the plugin yet) and a log folder.</summary>
    public static async Task<Rig> StartAsync(int cameras = 1, bool withLog = true, Action<FakeCoreContext>? setup = null)
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var publisher = new FakeEventPublisher();
        var folder = Path.Combine(Path.GetTempPath(), "oadm-lpc-tests", Guid.NewGuid().ToString("N"));
        var ctx = new FakeCoreContext { EventPublisher = publisher, DataDirectory = Path.Combine(folder, "datafolder", "plugin-data", LicensePlatesInfo.PluginId) };
        for (var i = 0; i < cameras; i++)
        {
            ctx.Add(new FakeDevice { Address = "10.0.0." + (10 + i).ToString(CultureInfo.InvariantCulture), Serial = "ACCC8E0000" + i.ToString("X2", CultureInfo.InvariantCulture) }, new AlpvCamera());
        }

        setup?.Invoke(ctx);
        if (withLog)
        {
            await ctx.PluginSettings.SetAsync(LicensePlatesPlugin.SettingsKey, PluginJson.Serialize(new LicensePlateSettings { Folder = Path.Combine(folder, "log") }, LicensePlatesJson.Options), CancellationToken.None);
        }

        var plugin = new LicensePlatesPlugin(new CameraOptions { RequestTimeout = TimeSpan.FromSeconds(30) }, time);
        await plugin.StartAsync(ctx, CancellationToken.None);
        return new Rig(ctx, plugin, publisher, time, folder);
    }

    public AlpvCamera Camera(int index) => (AlpvCamera)Ctx.Cameras[Ctx.DeviceList[index].Id];

    public Guid Id(int index) => Ctx.DeviceList[index].Id;

    public async Task<string?> CallAsync(string method, object? payload = null, ICallContext? caller = null) =>
        await Plugin.InvokeAsync(caller ?? Admin, method, payload is null ? null : PluginJson.Serialize(payload, LicensePlatesJson.Options), CancellationToken.None);

    public async Task<T> CallAsync<T>(string method, object? payload = null, ICallContext? caller = null) =>
        PluginJson.Parse<T>(await CallAsync(method, payload, caller), LicensePlatesJson.Options);

    public async Task<PlateTag> TagAsync(string name)
    {
        var reply = await CallAsync<SaveTagReply>(LicensePlatesMethods.SaveTag, new SaveTagRequest(new PlateTag { Name = name }));
        return reply.Tag ?? throw new InvalidOperationException(string.Join(", ", reply.Errors!.Values));
    }

    public async Task<Plate> PlateAsync(string text, params PlateTag[] tags)
    {
        var reply = await CallAsync<SavePlateReply>(LicensePlatesMethods.SavePlate, new SavePlateRequest(new Plate { Text = text, Name = "Car " + text, Tags = [.. tags.Select(t => t.Id)] }));
        return reply.Plate ?? throw new InvalidOperationException(string.Join(", ", reply.Errors!.Values));
    }

    public async Task DisposeAsync()
    {
        await Plugin.DisposeAsync();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());
}
