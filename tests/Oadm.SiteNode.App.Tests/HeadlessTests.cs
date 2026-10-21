using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.App.Services;
using Oadm.SiteNode.App.ViewModels;
using Oadm.SiteNode.App.Views;
using Oadm.Theme.Shell;

namespace Oadm.SiteNode.App.Tests;

/// <summary>The app's windows rendered offscreen with the real app and theme (screenshots to OADM_SCREENSHOT_DIR).</summary>
public sealed class HeadlessTests
{
    public static TheoryData<string> States => ["open", "server", "cannot-reach", "closed", "bound", "bound-offline", "identity", "no-service"];

    [Theory]
    [MemberData(nameof(States))]
    public async Task TheWindowShowsTheStateOnOnePage(string state)
    {
        var shown = await ShowAsync(StateOf(state), $"site-node-app-{state}.png");

        // Save of the footer is the one accent button, in every state.
        Assert.Equal(["Save"], shown.Primary);
        Assert.Contains("Close", shown.Texts);
        Assert.Contains("Node status", shown.Texts);
        Assert.Contains("OADM Site Node", shown.Texts); // the title bar
        Assert.DoesNotContain("Connection log", shown.Texts); // no activity part
        Assert.DoesNotContain("Show pairing code", shown.Texts); // the code shows by itself

        switch (state)
        {
            case "open":
                Assert.Contains("Not bound. Binding open for 23 more hours.", shown.Texts);
                Assert.Contains("Pairing code", shown.Texts);
                Assert.Contains("ABCD-EFGH", shown.Texts);
                Assert.Contains(NodeAppViewModel.TypeCode + " Valid for 8 more minutes.", shown.Texts);
                Assert.Contains("Connected to 10.0.0.5:5081", shown.Texts);
                break;
            case "server":
                Assert.Contains(NodeAppViewModel.EnterServer + " " + NodeAppViewModel.PortHint, shown.Texts);
                Assert.Contains("No server address yet", shown.Texts);
                Assert.Contains("ABCD-EFGH", shown.Texts); // right below the address field
                break;
            case "cannot-reach":
                Assert.Contains("Cannot reach 10.0.0.5:5081", shown.Texts); // below the address field
                Assert.Contains("Not connected: Cannot reach 10.0.0.5:5081", shown.Texts);
                break;
            case "closed":
                Assert.Contains("Binding is closed.", shown.Texts);
                Assert.Contains("Allow binding", shown.Texts);
                Assert.DoesNotContain("ABCD-EFGH", shown.Texts);
                break;
            case "bound":
                Assert.Contains("Connected to OADM-SRV", shown.Texts);
                Assert.Contains("Site Building B", shown.Texts);
                Assert.Contains(shown.Texts, t => t.StartsWith("Bound since ", StringComparison.Ordinal));
                Assert.Single(shown.Texts, t => t.Contains("Building B", StringComparison.Ordinal)); // each name once
                Assert.Single(shown.Texts, t => t.Contains("OADM-SRV", StringComparison.Ordinal));
                Assert.DoesNotContain("Pairing code", shown.Texts);
                break;
            case "bound-offline":
                Assert.Contains("Not connected: Cannot reach 10.0.0.5:5081", shown.Texts);
                break;
            case "identity":
                Assert.Contains(shown.Texts, t => t.StartsWith("OADM-SRV presents a new identity", StringComparison.Ordinal));
                Assert.Contains("Accept new server identity", shown.Texts);
                break;
            case "no-service":
                Assert.Contains("Service not running", shown.Texts);
                Assert.Contains("Start the OADM Site Node service.", shown.Texts);
                Assert.DoesNotContain("Networks", shown.Texts);
                Assert.DoesNotContain("Reset", shown.Texts);
                break;
        }

        if (state != "no-service")
        {
            // Every part on one page: server, networks (the select), ports (the table), Reset in the footer.
            Assert.Contains("Server address", shown.Texts);
            Assert.Contains("Networks", shown.Texts);
            Assert.Contains("Reach devices on", shown.Texts);
            Assert.Contains("Cameras", shown.Texts); // the select names the chosen card
            Assert.DoesNotContain("Extra networks", shown.Texts); // command line only
            Assert.Contains("Ports", shown.Texts);
            Assert.Contains("Connect to", shown.Texts);
            Assert.Contains("Node ID a1b2c3d4… · Version 3.7.0", shown.Texts);
            Assert.Contains("Reset", shown.Texts);
        }
    }

    [Fact]
    public async Task RequestsAndRelayProblemsShowInTheStatusAndTheTable()
    {
        var state = Copy(FakeChannel.Sample(), relays: [new RelayView(PortProtocol.Udp, 123, "NTP server", "Port in use", "UDP 123 is used by another program.")]);
        var shown = await ShowAsync(state, "site-node-app-requests.png");

        Assert.Contains("The server asks for 1 port.", shown.Texts);
        Assert.Contains("Requested", shown.Texts);
        Assert.Contains("Allow", shown.Texts);
        Assert.Contains("Dismiss", shown.Texts);
        Assert.Contains("NTP server, UDP 123: UDP 123 is used by another program.", shown.Texts); // the chip with its detail
    }

    [Fact]
    public async Task TheNetworkSelectListsAllCardsAndEveryCardWithItsAddresses()
    {
        var shown = await ShowAsync(FakeChannel.Sample(requests: false), "site-node-app-networks.png", openNetworks: true);

        Assert.Contains("All cards", shown.Texts);
        Assert.Contains("Office", shown.Texts);
        Assert.Contains("192.168.0.5/24", shown.Texts);
        Assert.Contains("10.0.0.7/24 · Default router", shown.Texts);
    }

    [Fact]
    public async Task CloseOfTheFooterClosesTheWindow()
    {
        var closed = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var channel = new FakeChannel();
            var vm = new NodeAppViewModel(channel, new FakeElevation(channel) { IsAdministrator = true }, new FakeDialogs());
            await vm.RefreshAsync();
            var window = new MainWindow();
            window.Attach(vm);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var wasClosed = false;
            window.Closed += (_, _) => wasClosed = true;
            var close = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Button>().Single(b => b.Content as string == "Close");
            close.Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            return wasClosed;
        }, CancellationToken.None);

        Assert.True(closed);
    }

    /// <summary>The node in each state (no requests, so only the state's own lines show).</summary>
    private static NodeView? StateOf(string state) => state switch
    {
        "open" => FakeChannel.Sample(requests: false),
        "server" => FakeChannel.Sample(serverAddress: "", connected: false, connectionText: "", requests: false),
        "cannot-reach" => FakeChannel.Sample(connected: false, connectionText: "Cannot reach 10.0.0.5:5081", requests: false),
        "closed" => FakeChannel.Sample(NodeBinding.Closed, requests: false),
        "bound" => FakeChannel.Sample(NodeBinding.Bound, requests: false),
        "bound-offline" => FakeChannel.Sample(NodeBinding.Bound, connected: false, connectionText: "Cannot reach 10.0.0.5:5081", requests: false),
        "identity" => FakeChannel.Sample(NodeBinding.Bound, newIdentity: "0123456789ABCDEF0123456789ABCDEF", requests: false),
        _ => null,
    };

    private static NodeView Copy(NodeView view, IReadOnlyList<RelayView> relays) => new()
    {
        NodeId = view.NodeId,
        Version = view.Version,
        HostName = view.HostName,
        Binding = view.Binding,
        ServerAddress = view.ServerAddress,
        BindingHoursLeft = view.BindingHoursLeft,
        Bound = view.Bound,
        ConnectionText = view.ConnectionText,
        Connected = view.Connected,
        Cards = view.Cards,
        AllCards = view.AllCards,
        ExtraSubnets = view.ExtraSubnets,
        Allowed = view.Allowed,
        Requested = view.Requested,
        Relays = relays,
        PairingCode = view.PairingCode,
        PairingCodeValidUntil = view.PairingCodeValidUntil,
        PairingCodeGeneration = view.PairingCodeGeneration,
    };

    /// <summary>The window as it opens (its own size): the texts it shows (the open network list too) and its accent buttons.</summary>
    private static Task<(List<string> Texts, List<string> Primary)> ShowAsync(NodeView? state, string screenshot, bool openNetworks = false) =>
        HeadlessEntry.Session.Dispatch(async () =>
        {
            var channel = new FakeChannel { State = state };
            var vm = new NodeAppViewModel(channel, new FakeElevation(channel) { IsAdministrator = true }, new FakeDialogs(),
                new Microsoft.Extensions.Time.Testing.FakeTimeProvider(FakeChannel.Now));
            await vm.RefreshAsync();
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var texts = new List<string>();
            if (openNetworks)
            {
                var select = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<ComboBox>().Single(c => c.Classes.Contains("multiSelect"));
                select.IsDropDownOpen = true;
                Dispatcher.UIThread.RunJobs();
                foreach (var visual in select.GetRealizedContainers().SelectMany(Avalonia.VisualTree.VisualExtensions.GetVisualDescendants))
                {
                    if (visual is TextBlock { Text: { Length: > 0 } text })
                        texts.Add(text);
                    else if (visual is CheckBox { Content: string content })
                        texts.Add(content);
                }
            }

            Save(window, screenshot);
            texts.AddRange(window.GetVisualDescendantsTexts());
            var primary = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window)
                .OfType<Button>()
                .Where(b => b.IsEffectivelyVisible && b.Classes.Contains("primary"))
                .Select(b => b.Content as string ?? "")
                .ToList();
            window.Close();
            return (texts, primary);
        }, CancellationToken.None);

    [Fact]
    public async Task TheTermsAreAcceptedOncePerComputer()
    {
        var folder = Path.Combine(Path.GetTempPath(), "oadm-site-node-app-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var store = new AppSettingsStore(folder);
            Assert.False(NodeTermsViewModel.IsAccepted(store.Load()));

            var result = await HeadlessEntry.Session.Dispatch(() =>
            {
                var window = new TermsWindow();
                var vm = new NodeTermsViewModel(store, store.Load());
                window.Attach(vm);
                bool? accepted = null;
                window.Completed += (_, value) => accepted = value;
                window.Show();
                Dispatcher.UIThread.RunJobs();
                Save(window, "site-node-app-terms.png");
                vm.AcceptCommand.Execute(null);
                window.Close();
                return (accepted, vm.Title, vm.License);
            }, CancellationToken.None);

            Assert.True(result.accepted);
            Assert.StartsWith("Terms of use", result.Title, StringComparison.Ordinal);
            Assert.Contains("Apache License", result.License, StringComparison.Ordinal);
            Assert.True(NodeTermsViewModel.IsAccepted(store.Load()));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task TheXOfTheTermsQuits()
    {
        var accepted = await HeadlessEntry.Session.Dispatch(() =>
        {
            var window = new TermsWindow();
            window.Attach(new NodeTermsViewModel(new AppSettingsStore(Path.GetTempPath()), new AppSettings()));
            bool? result = null;
            window.Completed += (_, value) => result = value;
            window.Show();
            window.Close();
            return result;
        }, CancellationToken.None);

        Assert.False(accepted);
    }

    private static void Save(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR") is not { Length: > 0 } folder)
            return;
        Directory.CreateDirectory(folder);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(folder, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
}

internal static class VisualTexts
{
    /// <summary>Every text the window shows (text blocks, selectable text, button and check box contents).</summary>
    public static List<string> GetVisualDescendantsTexts(this Window window)
    {
        var texts = new List<string>();
        foreach (var visual in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window))
        {
            switch (visual)
            {
                case TextBlock { Text: { Length: > 0 } text } when visual.IsEffectivelyVisible:
                    texts.Add(text);
                    break;
                case ContentControl { Content: string content } when visual.IsEffectivelyVisible:
                    texts.Add(content);
                    break;
            }
        }

        return texts;
    }
}
