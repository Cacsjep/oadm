using System.Net;

using Oadm.Plugins.DateAndTime.Model;
using Oadm.Plugins.DateAndTime.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.NtpServer.Tasks;

/// <summary>
/// Contributed task "Use OADM as NTP server": points the device's NTP client at the OADM server address on the interface
/// the device reaches (static server list = that one address). Steps: Check compatibility, Read NTP settings, Set NTP
/// server &lt;addr&gt;, Verify NTP settings. The VAPIX requests are the Date and time plugin's (TimePlanner / TimeClient).
/// </summary>
public sealed class UseOadmNtpServerTask : ITaskPlugin
{
    public const string StepCheckCompatibility = "Check compatibility";
    public const string StepReadNtp = "Read NTP settings";
    public const string StepVerifyNtp = "Verify NTP settings";
    public const string StepSetPrefix = "Set NTP server";

    private readonly Func<NtpServerService?> _service;
    private readonly TimeProvider _time;

    internal UseOadmNtpServerTask(Func<NtpServerService?> service, TimeProvider? time = null)
    {
        _service = service;
        _time = time ?? TimeProvider.System;
    }

    public string Id => NtpServerPluginInfo.UseTaskId;

    public string DisplayName => "Use OADM as NTP server";

    public string? IconKey => NtpServerPluginInfo.IconKey;

    public string Group => TaskGroups.Maintenance;

    public bool ShowInToolbar => false;

    public bool RequiresDialog => false;

    public bool CanRun(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TimeApis.HasNtpApi(device.Apis) || device.Apis.Supports(TimeApis.ParamCgi, TimeApis.ParamCgiBase);
    }

    /// <summary>Plain-language reason for the greyed menu entry when <see cref="CanRun"/> is false (cached data only).</summary>
    public string? NotSupportedReason(IDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return TaskSupportReasons.NeedsApi("the NTP API", device);
    }

    public string GetTaskName(string? payloadJson) =>
        _service()?.NameAddress is { } address ? $"Use OADM NTP server {address}" : "Use OADM NTP server";

    public async Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(device);

        // Which OADM address the device reaches (local: no device request).
        var service = _service();
        IPAddress? server = null;
        string? problem;
        if (service is null)
        {
            problem = "The NTP server plugin is not running. Nothing was changed.";
        }
        else
        {
            var deviceAddress = await ResolveDeviceAddressAsync(device.Address, ct).ConfigureAwait(false);
            (server, problem) = deviceAddress is null
                ? (null, $"The address {device.Address} cannot be resolved on the server. Nothing was changed.")
                : service.ServerAddressFor(deviceAddress);
        }

        var setStep = server is null ? StepSetPrefix : $"{StepSetPrefix} {server}";
        ctx.PlanSteps(StepCheckCompatibility, StepReadNtp, setStep, StepVerifyNtp);

        var apis = await ctx.StepAsync(StepCheckCompatibility, async step =>
        {
            if (server is null)
            {
                throw new InvalidOperationException(problem);
            }

            var list = await ctx.Vapix.GetApiListAsync(ct).ConfigureAwait(false);
            if (!TimeApis.HasNtpApi(list))
            {
                list.Require(TimeApis.ParamCgi, TimeApis.ParamCgiBase);
            }

            step.Complete(TimeApis.HasNtpApi(list) ? $"ntp {list.FindApi(TimeApis.Ntp, 1)?.Version}" : "param.cgi");
            return list;
        }).ConfigureAwait(false);

        var current = await ctx.StepAsync(StepReadNtp, async step =>
        {
            var read = await ReadAsync(ctx.Vapix, apis, ct).ConfigureAwait(false);
            step.Complete(Describe(read));
            return read;
        }).ConfigureAwait(false);
        ctx.Log(TaskLogLevel.Info, "Current: " + Describe(current) + ".");

        var address = server!.ToString();
        // Only the NTP server changes; the device keeps its time zone.
        var payload = new DateTimePayload(TimeZone: null, Mode: TimeMode.Ntp, Ntp: new NtpSettings(NtpSource.Static, [address]), TimeZoneUnchanged: true);
        var plan = TimePlanner.Build(payload, apis, current, null, _time.GetUtcNow().Year);
        var section = plan.Find(SectionKind.Ntp)!;
        if (section.SkipReason is not null)
        {
            ctx.SkipStep(setStep, $"Already uses {address}");
            ctx.SkipStep(StepVerifyNtp, "Nothing was changed.");
            return;
        }

        await ctx.StepAsync(setStep, async step =>
        {
            foreach (var request in section.Requests)
            {
                await TimeClient.SendAsync(ctx.Vapix, request, ct).ConfigureAwait(false);
            }

            step.Complete("NTP server " + address);
            return true;
        }).ConfigureAwait(false);
        ctx.Log(TaskLogLevel.Info, $"NTP server set to {address} (OADM).");

        await ctx.StepAsync(StepVerifyNtp, async step =>
        {
            var after = await ReadAsync(ctx.Vapix, apis, ct).ConfigureAwait(false);
            var ok = after.NtpEnabled != false
                && after.NtpSource != NtpSource.Dhcp
                && after.NtpServers.Contains(address, StringComparer.OrdinalIgnoreCase);
            if (ok)
            {
                step.Complete(Describe(after));
            }
            else
            {
                step.Warn("The device reports " + Describe(after) + " instead of " + address + ".");
            }

            return ok;
        }).ConfigureAwait(false);
    }

    internal static async Task<CurrentTimeSettings> ReadAsync(IVapixClient vapix, IReadOnlyList<DeviceApi> apis, CancellationToken ct) =>
        TimeApis.HasNtpApi(apis)
            ? await TimeClient.ReadNtpInfoAsync(vapix, apis, new CurrentTimeSettings(), ct).ConfigureAwait(false)
            : await TimeClient.ReadParametersAsync(vapix, apis, new CurrentTimeSettings(), ct).ConfigureAwait(false);

    /// <summary>"NTP servers 10.0.0.1, pool.ntp.org", "NTP servers from DHCP", "NTP off".</summary>
    internal static string Describe(CurrentTimeSettings settings) =>
        settings.NtpEnabled == false ? "NTP off"
        : settings.NtpSource == NtpSource.Dhcp ? "NTP servers from DHCP"
        : settings.NtpServers.Count == 0 ? "No NTP server"
        : "NTP servers " + string.Join(", ", settings.NtpServers);

    /// <summary>The device's IP from its OADM address ("10.0.0.48", "camera.example.com:8443", "[fd00::1]:443").</summary>
    internal static async Task<IPAddress?> ResolveDeviceAddressAsync(string address, CancellationToken ct)
    {
        if (IPAddress.TryParse(address, out var ip))
        {
            return ip;
        }

        var text = address.Contains("://", StringComparison.Ordinal) ? address : "http://" + address;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var host = uri.Host.Trim('[', ']');
        if (IPAddress.TryParse(host, out ip))
        {
            return ip;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            return addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.FirstOrDefault();
        }
        catch (System.Net.Sockets.SocketException)
        {
            return null;
        }
    }
}
