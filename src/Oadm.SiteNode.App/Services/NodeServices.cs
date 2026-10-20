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

    public Task<AdminResponse> SendAsync(AdminRequest request, CancellationToken ct) => _client.SendAsync(request, ct);
}

/// <summary>
/// Changes and the pairing code need an administrator of this computer (docs/sites.md, "App"). The app itself runs as the
/// user; a change runs a copy of the app elevated (UAC on Windows, pkexec on Linux) that sends that one request and hands
/// the answer back: on Linux over the copy's standard output (the answer holds the pairing code: never a file in a shared
/// folder), on Windows in a file in the user's own temp folder (UAC cannot redirect the output).
/// </summary>
public interface IElevation
{
    /// <summary>True when the app already runs as an administrator: it sends changes itself.</summary>
    bool IsAdministrator { get; }

    /// <summary>The answer; never throws (a failure is an answer with the reason, a declined prompt <see cref="SystemElevation.Cancelled"/>).</summary>
    Task<AdminResponse> RunAsync(AdminRequest request, CancellationToken ct);
}

public sealed class SystemElevation(string? dataDirectory) : IElevation
{
    public const string RequestArgument = "--admin-request";
    public const string OutArgument = "--out";

    /// <summary>The value of <see cref="OutArgument"/> that sends the answer to the standard output.</summary>
    public const string StandardOutput = "-";

    /// <summary>The line of the standard output that carries the answer (base64 of the JSON).</summary>
    public const string AnswerPrefix = "OADM-ANSWER ";

    /// <summary>The administrator prompt was declined: nothing is shown.</summary>
    public const string Cancelled = "Cancelled.";

    /// <summary>The elevated copy ended without an answer.</summary>
    public const string NotMade = "The change could not be made.";

    /// <summary>No pkexec on this computer.</summary>
    public const string NoPkexec = "Changes need pkexec. Install the pkexec package (or policykit-1) and try again.";

    /// <summary>Windows: the user declined the UAC prompt (ERROR_CANCELLED).</summary>
    public const int UacDeclined = 1223;

    public bool IsAdministrator => Environment.IsPrivilegedProcess || (OperatingSystem.IsWindows() && IsWindowsAdministrator());

    public async Task<AdminResponse> RunAsync(AdminRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null)
                return AdminResponse.Fail(NotMade);
            return OperatingSystem.IsWindows()
                ? await RunWindowsAsync(exe, request, ct).ConfigureAwait(false)
                : await RunPkexecAsync(exe, request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AdminResponse.Fail(Cancelled);
        }
#pragma warning disable CA1031 // the app shows the failure; it never crashes on a change
        catch (Exception)
#pragma warning restore CA1031
        {
            return AdminResponse.Fail(NotMade);
        }
    }

    private async Task<AdminResponse> RunWindowsAsync(string exe, AdminRequest request, CancellationToken ct)
    {
        // %TEMP% on Windows is the user's own folder (AppData\Local\Temp): only the user and administrators read it.
        var output = Path.Combine(Path.GetTempPath(), "oadm-site-node-" + Guid.NewGuid().ToString("N") + ".json");
        var start = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in Arguments(request, output))
            start.ArgumentList.Add(argument);
        try
        {
            Process? process;
            try
            {
                process = Process.Start(start);
            }
            catch (Win32Exception e)
            {
                return AdminResponse.Fail(e.NativeErrorCode == UacDeclined ? Cancelled : NotMade);
            }

            if (process is null)
                return AdminResponse.Fail(NotMade);
            using (process)
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var answer = File.Exists(output) ? await File.ReadAllTextAsync(output, ct).ConfigureAwait(false) : null;
            return Interpret(answer is null ? null : ParseJson(answer));
        }
        finally
        {
            Delete(output);
        }
    }

    private async Task<AdminResponse> RunPkexecAsync(string exe, AdminRequest request, CancellationToken ct)
    {
        var start = new ProcessStartInfo("pkexec") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(exe);
        foreach (var argument in Arguments(request, StandardOutput))
            start.ArgumentList.Add(argument);
        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Win32Exception)
        {
            return AdminResponse.Fail(NoPkexec);
        }

        if (process is null)
            return AdminResponse.Fail(NoPkexec);
        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            return InterpretPkexec(process.ExitCode, text);
        }
    }

    private List<string> Arguments(AdminRequest request, string output)
    {
        var arguments = new List<string> { RequestArgument, Encode(request), OutArgument, output };
        if (dataDirectory is not null)
            arguments.AddRange(["--data-dir", dataDirectory]);
        return arguments;
    }

    /// <summary>
    /// What pkexec's run means: the copy's answer when it wrote one; else 126 / 127 (the user dismissed the prompt or is not
    /// allowed) is <see cref="Cancelled"/>, anything else <see cref="NotMade"/>.
    /// </summary>
    public static AdminResponse InterpretPkexec(int exitCode, string? standardOutput)
    {
        if (ParseAnswerLine(standardOutput) is { } answer)
            return answer;
        return exitCode is 126 or 127 ? AdminResponse.Fail(Cancelled) : AdminResponse.Fail(NotMade);
    }

    /// <summary>The answer of the copy, or <see cref="NotMade"/> when it ended without one.</summary>
    public static AdminResponse Interpret(AdminResponse? answer) => answer ?? AdminResponse.Fail(NotMade);

    /// <summary>The answer line in the copy's standard output, or null.</summary>
    public static AdminResponse? ParseAnswerLine(string? standardOutput)
    {
        if (string.IsNullOrEmpty(standardOutput))
            return null;
        foreach (var line in standardOutput.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith(AnswerPrefix, StringComparison.Ordinal))
                continue;
            try
            {
                return JsonSerializer.Deserialize<AdminResponse>(Convert.FromBase64String(trimmed[AnswerPrefix.Length..]), AdminJson.Options);
            }
            catch (Exception e) when (e is FormatException or JsonException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>The answer line for the standard output.</summary>
    public static string AnswerLine(AdminResponse response) =>
        AnswerPrefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(response, AdminJson.Options));

    private static AdminResponse? ParseJson(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<AdminResponse>(text, AdminJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The elevated copy: sends the one request and writes the answer. Returns the exit code.</summary>
    public static Task<int> AnswerAsync(string[] args, AdminEndpoint endpoint) =>
        AnswerAsync(args, request => new AdminClient(endpoint).SendAsync(request, CancellationToken.None), Console.Out);

    /// <summary>
    /// The elevated copy with its sender and standard output: <c>--out -</c> writes the answer line to
    /// <paramref name="standardOutput"/>, else the JSON to the file (Windows: the user's own temp folder).
    /// </summary>
    public static async Task<int> AnswerAsync(string[] args, Func<AdminRequest, Task<AdminResponse>> send, TextWriter standardOutput)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(send);
        ArgumentNullException.ThrowIfNull(standardOutput);
        var request = Value(args, RequestArgument) is { } encoded ? Decode(encoded) : null;
        var output = Value(args, OutArgument);
        if (request is null || output is null)
            return 2;
        AdminResponse response;
        try
        {
            response = await send(request).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // the app gets an answer whatever happened
        catch (Exception)
#pragma warning restore CA1031
        {
            response = AdminResponse.Fail(AdminTexts.ServiceNotRunning);
        }

        if (output == StandardOutput)
        {
            await standardOutput.WriteLineAsync(AnswerLine(response)).ConfigureAwait(false);
            await standardOutput.FlushAsync().ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(response, AdminJson.Options)).ConfigureAwait(false);
        }

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
