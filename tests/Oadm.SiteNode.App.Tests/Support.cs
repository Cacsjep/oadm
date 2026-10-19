using Avalonia;
using Avalonia.Headless;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.App.Services;
using Oadm.SiteNode.App.ViewModels;

namespace Oadm.SiteNode.App.Tests;

/// <summary>The service as the app sees it: a state to answer with and every request it got.</summary>
internal sealed class FakeChannel : INodeChannel
{
    public NodeView? State { get; set; } = Sample();

    public List<(AdminRequest Request, bool AsAdmin)> Requests { get; } = [];

    public Func<AdminRequest, AdminResponse>? Answer { get; set; }

    public bool NextAsAdmin { get; set; }

    public Task<AdminResponse> SendAsync(AdminRequest request, CancellationToken ct)
    {
        Requests.Add((request, NextAsAdmin));
        NextAsAdmin = false;
        if (State is null)
            return Task.FromResult(AdminResponse.Fail(AdminTexts.ServiceNotRunning));
        if (Answer?.Invoke(request) is { } answer)
            return Task.FromResult(answer);
        return Task.FromResult(request.Command switch
        {
            AdminCommands.PairingCode => new AdminResponse { Ok = true, PairingCode = "ABCD-EFGH" },
            AdminCommands.Log => new AdminResponse
            {
                Ok = true,
                Log = [new ConnectionLogEntry(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero), "OADM-SRV", "192.168.0.20:443", "Short", "Connected")],
            },
            _ => new AdminResponse { Ok = true, State = State },
        });
    }

    public static NodeView Sample(NodeBinding binding = NodeBinding.Open) => new()
    {
        NodeId = "a1b2c3d4e5f60718",
        Version = "3.7.0",
        HostName = "site-pc",
        Binding = binding,
        Mode = NodeLinkMode.Connect,
        ServerAddress = "10.0.0.5",
        BindingHoursLeft = 22.6,
        Bound = binding == NodeBinding.Bound ? new BoundServerView("OADM-SRV", "AB12", new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero), "Building B") : null,
        ConnectionText = "Connected to 10.0.0.5:5081",
        Connected = true,
        Cards =
        [
            new CardView("cam", "Cameras", ["192.168.0.5/24"], true, false),
            new CardView("office", "Office", ["10.0.0.7/24"], false, true),
        ],
        ExtraSubnets = ["10.20.0.0/16"],
        Allowed = [new(PortUse.Connect, PortProtocol.Tcp, 80), new(PortUse.Connect, PortProtocol.Tcp, 443), new(PortUse.Connect, PortProtocol.Tcp, 554), new(PortUse.Listen, PortProtocol.Udp, 123, "NTP server")],
        Requested = [new(PortUse.Connect, PortProtocol.Tcp, 8443, "camera 192.168.0.30")],
        Relays = [new RelayView(PortProtocol.Udp, 123, "NTP server", "Running", "")],
        ShortConnections = 3,
        PermanentConnections = 12,
    };
}

/// <summary>Elevation that forwards to the fake service as an administrator, or is declined.</summary>
internal sealed class FakeElevation(FakeChannel channel) : IElevation
{
    public bool IsAdministrator { get; set; }

    public bool Decline { get; set; }

    public int Runs { get; private set; }

    public Task<AdminResponse> RunAsync(AdminRequest request, CancellationToken ct)
    {
        Runs++;
        if (Decline)
            return Task.FromResult(AdminResponse.Fail(SystemElevation.Cancelled));
        channel.NextAsAdmin = true;
        return channel.SendAsync(request, ct);
    }
}

internal sealed class FakeDialogs : IAppDialogs
{
    public bool Answer { get; set; } = true;

    public List<string> Asked { get; } = [];

    public Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        Asked.Add(message);
        return Task.FromResult(Answer);
    }
}

/// <summary>One headless app per test process: the real app class in the dark appearance.</summary>
public static class HeadlessEntry
{
    private static readonly Lazy<HeadlessUnitTestSession> Shared = new(() =>
    {
        App.AppearanceOverride = Environment.GetEnvironmentVariable("OADM_TEST_APPEARANCE") == "Light" ? Oadm.Theme.Appearance.Light : Oadm.Theme.Appearance.Dark;
        return HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry));
    });

    public static HeadlessUnitTestSession Session => Shared.Value;

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}
