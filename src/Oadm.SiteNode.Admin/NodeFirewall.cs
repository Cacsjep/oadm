using System.Diagnostics;
using System.Globalization;

namespace Oadm.SiteNode.Admin;

/// <summary>
/// The Windows Firewall rule of "Wait for the server" (docs/sites.md, "Packaging"): the MSI opens no port (the node
/// connects out); turning on Wait adds an inbound rule for the node's port, profiles Domain and Private, and turning it
/// off removes it. The service runs as its own unprivileged account and cannot change the firewall, so the administrator's process that
/// sent the change does it: the elevated copy of the app, the app running as administrator, or the command line. Other
/// systems: nothing (the .deb changes no firewall).
/// </summary>
public static class NodeFirewall
{
    public const string RuleName = "OADM Site Node (TCP 5081)";
    public const string Profiles = "domain,private";

    /// <summary>The node's exe next to the app or the command line (both are installed in one folder).</summary>
    public static string DefaultProgramPath => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "oadm-site-node.exe" : "oadm-site-node");

    /// <summary>
    /// The netsh calls after <paramref name="request"/> answered <paramref name="response"/>: none unless a server change
    /// went through. Mode Wait: delete the rule (no duplicates), then add it for the port the node waits on. Mode Connect:
    /// delete it. The rule name stays the same for another port, so one delete always finds it.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> Commands(AdminRequest request, AdminResponse response, string programPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        if (request.Command != AdminCommands.SetServer || !response.Ok)
            return [];
        IReadOnlyList<string> delete = ["advfirewall", "firewall", "delete", "rule", "name=" + RuleName];
        if (request.Arg("mode") != "wait")
            return [delete];
        var port = response.State?.ListenPort is > 0 and <= 65535 ? response.State.ListenPort : 5081;
        var add = new List<string>
        {
            "advfirewall", "firewall", "add", "rule",
            "name=" + RuleName,
            "dir=in",
            "action=allow",
            "protocol=TCP",
            "localport=" + port.ToString(CultureInfo.InvariantCulture),
        };
        if (programPath.Length > 0 && File.Exists(programPath))
            add.Add("program=" + programPath);
        add.Add("profile=" + Profiles);
        add.Add("enable=yes");
        return [delete, add];
    }

    /// <summary>
    /// Runs the calls of <see cref="Commands"/> on Windows. Returns null, or why the rule could not be added (the change
    /// itself stays done; the user sees the text). A delete that finds no rule is fine.
    /// </summary>
    public static async Task<string?> ApplyAsync(AdminRequest request, AdminResponse response, CancellationToken ct,
        Func<IReadOnlyList<string>, CancellationToken, Task<int>>? run = null, string? programPath = null)
    {
        if (run is null && !OperatingSystem.IsWindows())
            return null;
        run ??= RunNetshAsync;
        var commands = Commands(request, response, programPath ?? DefaultProgramPath);
        for (var i = 0; i < commands.Count; i++)
        {
            int exit;
            try
            {
                exit = await run(commands[i], ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                exit = -1;
            }

            var isAdd = commands[i].Count > 2 && commands[i][2] == "add";
            if (isAdd && exit != 0)
                return "The firewall rule for TCP 5081 could not be added. Allow the port in Windows Firewall.";
        }

        return null;
    }

    private static async Task<int> RunNetshAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo("netsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("netsh did not start.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(output, error).ConfigureAwait(false);
        return process.ExitCode;
    }
}
