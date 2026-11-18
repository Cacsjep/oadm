using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Grpc.Core;
using Grpc.Net.Client;

using Oadm.Contracts.V1;

if (args.Length != 2)
    throw new ArgumentException("Pass the released OADM server and extracted desktop bridge.");

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var ct = timeout.Token;
var data = Directory.CreateTempSubdirectory("oadm-desktop-mcp-");
try
{
    var grpcPort = FreePort();
    int mcpPort;
    do { mcpPort = FreePort(); } while (mcpPort == grpcPort);

    var start = StartInfo(args[0]);
    start.ArgumentList.Add($"--Oadm:DataDir={data.FullName}");
    start.ArgumentList.Add($"--Oadm:ListenUrl=https://127.0.0.1:{grpcPort}");
    start.ArgumentList.Add("--Oadm:PluginDir=plugins");
    using var host = Process.Start(start) ?? throw new InvalidOperationException("Could not start OADM.");
    // Drain both streams without printing bootstrap credentials.
    var stdout = host.StandardOutput.ReadToEndAsync(ct);
    var stderr = host.StandardError.ReadToEndAsync(ct);
    try
    {
        var tlsPath = Path.Combine(data.FullName, "server-tls.json");
        await WaitAsync(() =>
        {
            if (host.HasExited)
                throw new InvalidOperationException($"OADM exited with {host.ExitCode}.");
            return Task.FromResult(File.Exists(tlsPath));
        }, ct);
        using var tls = JsonDocument.Parse(await File.ReadAllTextAsync(tlsPath, ct));
        var pem = tls.RootElement.EnumerateObject().Single(p =>
            p.Name.Equals("certificatePem", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
        using var certificate = X509Certificate2.CreateFromPem(pem);
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                presented is not null && certificate.RawData.AsSpan().SequenceEqual(presented.RawData),
        };
        using var channel = GrpcChannel.ForAddress($"https://127.0.0.1:{grpcPort}", new GrpcChannelOptions { HttpHandler = handler });
        var auth = new AuthService.AuthServiceClient(channel);
        string? version = null;
        await WaitAsync(async () =>
        {
            try
            {
                version = (await auth.StatusAsync(new Empty(), cancellationToken: ct)).Version;
                return true;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable) { return false; }
        }, ct);

        var login = await auth.CreateFirstAdminAsync(new CreateFirstAdminRequest
        {
            UserName = "Lab",
            Password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "a1!",
        }, cancellationToken: ct);
        var headers = new Metadata { { "authorization", "Bearer " + login.Token } };
        var devices = await new DeviceService.DeviceServiceClient(channel).ListAsync(new Empty(), headers, cancellationToken: ct);
        Require(devices.Devices.Count == 0, "The fixture must start with an empty device inventory.");

        var mcp = new McpService.McpServiceClient(channel);
        var state = await mcp.GetAsync(new Empty(), headers, cancellationToken: ct);
        var loopback = state.Interfaces.First(i => i.Addresses.Any(a => a.StartsWith("127.", StringComparison.Ordinal)));
        await mcp.SetAsync(new McpSettings { Enabled = true, Port = mcpPort, ListenOn = loopback.Id }, headers, cancellationToken: ct);
        await WaitAsync(async () =>
        {
            var current = await mcp.GetAsync(new Empty(), headers, cancellationToken: ct);
            return current.Running && current.HasCaCertificate;
        }, ct);

        var ca = Path.Combine(data.FullName, "oadm-ca.pem");
        await File.WriteAllTextAsync(ca, (await mcp.GetCaCertificateAsync(new Empty(), headers, cancellationToken: ct)).Pem, ct);
        var keys = new AccessKeyService.AccessKeyServiceClient(channel);
        var key = await keys.CreateAsync(new CreateAccessKeyRequest
        {
            Name = "Desktop bridge fixture",
            RoleId = "00000000-0000-0000-0000-000000000003",
            Days = 30,
            AllDevices = true,
        }, headers, cancellationToken: ct);

        int count;
        try
        {
            using var init = await CallAsync("initialize", new { protocolVersion = "2025-11-25" });
            Require(init.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString() == "OADM",
                "The bridge did not initialize with OADM.");
            using var list = await CallAsync("tools/list", new { });
            var tools = list.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()).ToHashSet();
            count = tools.Count;
            Require(tools.Contains("oadm_server_info") && !tools.Contains("oadm_task_run"), "The Viewer key has unexpected tools.");
            using var read = await CallAsync("tools/call", new { name = "oadm_server_info", arguments = new { } });
            var result = read.RootElement.GetProperty("result");
            Require(!result.TryGetProperty("isError", out var isError) || !isError.GetBoolean(), "The Viewer read failed.");
        }
        finally
        {
            await keys.DeleteAsync(new AccessKeyIdRequest { Id = key.Key.Id }, headers, deadline: DateTime.UtcNow.AddSeconds(10));
        }
        using var revoked = await CallAsync("tools/list", new { });
        Require(revoked.RootElement.GetProperty("error").GetProperty("message").GetString() == "The OADM access key was refused.",
            "The revoked key was not refused.");
        Console.WriteLine($"Packaged bridge passed against OADM {version}. Viewer listed {count} tools, read server info, and was refused after revocation.");

        async Task<JsonDocument> CallAsync(string method, object parameters)
        {
            var bridge = StartInfo(args[1]);
            bridge.RedirectStandardInput = true;
            bridge.ArgumentList.Add($"https://localhost:{mcpPort}/mcp");
            bridge.Environment["OADM_MCP_CA"] = ca;
            bridge.Environment["OADM_MCP_KEY"] = key.Secret;
            using var process = Process.Start(bridge) ?? throw new InvalidOperationException("Could not start the bridge.");
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(ct);
                var errors = process.StandardError.ReadToEndAsync(ct);
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }).AsMemory(), ct);
                process.StandardInput.Close();
                await process.WaitForExitAsync(ct);
                var response = await output;
                Require(!response.Contains(key.Secret, StringComparison.Ordinal) && !(await errors).Contains(key.Secret, StringComparison.Ordinal),
                    "The bridge printed its access key.");
                Require(process.ExitCode == 0, $"The bridge exited with {process.ExitCode}.");
                return JsonDocument.Parse(response);
            }
            finally { await StopAsync(process); }
        }
    }
    finally
    {
        await StopAsync(host);
        try { await Task.WhenAll(stdout, stderr); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
finally { data.Delete(recursive: true); }

static ProcessStartInfo StartInfo(string path)
{
    var executable = Path.GetFullPath(path);
    return new ProcessStartInfo(executable)
    {
        WorkingDirectory = Path.GetDirectoryName(executable),
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
}

static async Task StopAsync(Process process)
{
    if (!process.HasExited) process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync();
}

static async Task WaitAsync(Func<Task<bool>> probe, CancellationToken ct)
{
    while (!await probe()) await Task.Delay(250, ct);
}

static int FreePort()
{
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
