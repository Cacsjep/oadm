using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

using Oadm.SiteNode.Admin;

namespace Oadm.SiteNode.App.Services;

/// <summary>The local channel to the service (a fake in tests).</summary>
public interface INodeChannel
{
    Task<AdminResponse> SendAsync(AdminRequest request, CancellationToken ct);
}

public sealed class AdminChannel(AdminEndpoint endpoint) : INodeChannel
{
    private readonly AdminClient _client = new(endpoint);

    /// <summary>Sends the request; a server change of the app running as administrator also sets the firewall rule.</summary>
    public async Task<AdminResponse> SendAsync(AdminRequest request, CancellationToken ct)
    {
        var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        return await WithFirewallAsync(request, response, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// "Wait for the server" opens TCP 5081 in Windows Firewall, "Connect to the server" closes it (the service runs as
    /// its own unprivileged account and cannot). A rule that could not be added is the answer's error; the change itself is done.
    /// </summary>
    public static async Task<AdminResponse> WithFirewallAsync(AdminRequest request, AdminResponse response, CancellationToken ct,
        Func<IReadOnlyList<string>, CancellationToken, Task<int>>? run = null, string? programPath = null) =>
        await NodeFirewall.ApplyAsync(request, response, ct, run, programPath).ConfigureAwait(false) is { } problem
            ? AdminResponse.Fail(problem)
            : response;
}

/// <summary>
/// Changes and the pairing code need an administrator of this computer (docs/sites.md, "App"). The app itself runs as the
/// user; a change runs a copy of the app elevated (UAC on Windows, pkexec on Linux) that sends that one request and
/// writes the answer to a file the app reads.
/// </summary>
public interface IElevation
{
    /// <summary>True when the app already runs as an administrator: it sends changes itself.</summary>
    bool IsAdministrator { get; }

    Task<AdminResponse> RunAsync(AdminRequest request, CancellationToken ct);
}

public sealed class SystemElevation(string? dataDirectory) : IElevation
{
    public const string RequestArgument = "--admin-request";
    public const string OutArgument = "--out";
    public const string Cancelled = "Cancelled.";

    public bool IsAdministrator => Environment.IsPrivilegedProcess || (OperatingSystem.IsWindows() && IsWindowsAdministrator());

    public async Task<AdminResponse> RunAsync(AdminRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The app's path is not known.");
        var output = Path.Combine(Path.GetTempPath(), "oadm-site-node-" + Guid.NewGuid().ToString("N") + ".json");
        var arguments = new List<string> { RequestArgument, Encode(request), OutArgument, output };
        if (dataDirectory is not null)
            arguments.AddRange(["--data-dir", dataDirectory]);
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            start = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
        }
        else
        {
            start = new ProcessStartInfo("pkexec") { UseShellExecute = false };
            start.ArgumentList.Add(exe);
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return AdminResponse.Fail(Cancelled);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            if (!File.Exists(output))
                return AdminResponse.Fail(Cancelled);
            return JsonSerializer.Deserialize<AdminResponse>(await File.ReadAllTextAsync(output, ct).ConfigureAwait(false), AdminJson.Options)
                ?? AdminResponse.Fail(AdminTexts.ServiceNotRunning);
        }
        catch (Win32Exception)
        {
            return AdminResponse.Fail(Cancelled); // UAC declined, or no pkexec
        }
        finally
        {
            try
            {
                File.Delete(output);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The elevated copy: sends the one request and writes the answer. Returns the exit code.</summary>
    public static async Task<int> AnswerAsync(string[] args, AdminEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(args);
        var request = Value(args, RequestArgument) is { } encoded ? Decode(encoded) : null;
        var output = Value(args, OutArgument);
        if (request is null || output is null)
            return 2;
        var response = await new AdminClient(endpoint).SendAsync(request, CancellationToken.None).ConfigureAwait(false);
        response = await AdminChannel.WithFirewallAsync(request, response, CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(response, AdminJson.Options)).ConfigureAwait(false);
        return response.Ok ? 0 : 1;
    }

    public static string Encode(AdminRequest request) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(request, AdminJson.Options));

    public static AdminRequest? Decode(string encoded)
    {
        try
        {
            return JsonSerializer.Deserialize<AdminRequest>(Convert.FromBase64String(encoded), AdminJson.Options);
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    public static string? Value(string[] args, string name)
    {
        ArgumentNullException.ThrowIfNull(args);
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool IsWindowsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>The app's own settings (the accepted terms), in the user's local application data.</summary>
public sealed class AppSettings
{
    public string AcceptedTerms { get; set; } = "";
}

public sealed class AppSettingsStore(string folder)
{
    public const string FileName = "app-settings.json";

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OADM Site Node");

    public AppSettings Load()
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path, Encoding.UTF8)) ?? new AppSettings() : new AppSettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(settings), Encoding.UTF8);
    }
}
