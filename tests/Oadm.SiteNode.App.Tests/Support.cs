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
            // Like the service: the saved address, and the pairing code while the node is not bound.
            AdminCommands.SetServer => SetServer(request.Arg("address")),
            AdminCommands.Log => new AdminResponse
            {
                Ok = true,
                Log = [new ConnectionLogEntry(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero), "OADM-SRV", "192.168.0.20:443", "Short", "Connected")],
            },
            _ => new AdminResponse { Ok = true, State = State },
        });
    }

    private AdminResponse SetServer(string address)
    {
        var before = State!;
        State = Sample(before.Binding, serverAddress: address, connected: before.Connected, connectionText: before.ConnectionText,
            newIdentity: before.NewServerIdentity, requests: before.Requested.Count > 0, allCards: before.AllCards);
        return new AdminResponse { Ok = true, State = State, PairingCode = before.Binding == NodeBinding.Open ? "ABCD-EFGH" : "" };
    }

    /// <summary>
    /// A node with two cards (the camera card chosen, unless <paramref name="allCards"/>), the default ports, an NTP relay and
    /// (unless <paramref name="requests"/> is false) a request.
    /// </summary>
    public static NodeView Sample(
        NodeBinding binding = NodeBinding.Open,
        string serverAddress = "10.0.0.5",
        bool connected = true,
        string connectionText = "Connected to 10.0.0.5:5081",
        string newIdentity = "",
        bool requests = true,
        bool allCards = false,
        long codeGeneration = 1) => new()
    {
        NodeId = "a1b2c3d4e5f60718",
        Version = "3.7.0",
        HostName = "site-pc",
        Binding = binding,
        ServerAddress = serverAddress,
        BindingHoursLeft = 22.6,
        Bound = binding == NodeBinding.Bound ? new BoundServerView("OADM-SRV", "AB12", new DateTimeOffset(2026, 10, 19, 8, 0, 0, TimeSpan.Zero), "Building B") : null,
        ConnectionText = connectionText,
        Connected = connected,
        NewServerIdentity = newIdentity,
        AllCards = allCards,
        Cards =
        [
            new CardView("cam", "Cameras", ["192.168.0.5/24"], true, false),
            new CardView("office", "Office", ["10.0.0.7/24"], allCards, true),
        ],
        ExtraSubnets = ["10.20.0.0/16"],
        Allowed = [new(PortUse.Connect, PortProtocol.Tcp, 80), new(PortUse.Connect, PortProtocol.Tcp, 443), new(PortUse.Connect, PortProtocol.Tcp, 554), new(PortUse.Listen, PortProtocol.Udp, 123, "NTP server")],
        Requested = requests ? [new(PortUse.Connect, PortProtocol.Tcp, 8443, "camera 192.168.0.30")] : [],
        Relays = [new RelayView(PortProtocol.Udp, 123, "NTP server", "Running", "")],
        ShortConnections = 3,
        PermanentConnections = 12,
        PairingCodeGeneration = codeGeneration,
    };
}

/// <summary>Elevation that forwards to the fake service as an administrator, or is declined.</summary>
internal sealed class FakeElevation(FakeChannel channel) : IElevation
{
    public bool IsAdministrator { get; set; }

    public bool Decline { get; set; }

    public int Runs { get; private set; }

    /// <summary>Set: the prompt stays open until the test completes it.</summary>
    public TaskCompletionSource? Prompt { get; set; }

    public async Task<AdminResponse> RunAsync(AdminRequest request, CancellationToken ct)
    {
        Runs++;
        if (Prompt is { } prompt)
            await prompt.Task;
        if (Decline)
            return AdminResponse.Fail(SystemElevation.Cancelled);
        channel.NextAsAdmin = true;
        return await channel.SendAsync(request, ct);
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
