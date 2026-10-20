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
    public static TheoryData<string> States => ["open", "open-code", "saved-code", "server", "cannot-reach", "closed", "bound", "bound-offline", "identity", "no-service"];

    [Theory]
    [MemberData(nameof(States))]
    public async Task TheGeneralTabAndTheNodeStatusShowTheState(string state)
    {
        var shown = await ShowAsync(StateOf(state), NodeTab.General, $"site-node-app-{state}.png", showCode: state == "open-code",
            saveAddress: state == "saved-code" ? "10.0.0.5" : null);

        // Save of the footer is the one accent button, in every state.
        Assert.Equal(["Save"], shown.Primary);
        Assert.Contains("Close", shown.Texts);
        Assert.Contains("Node status", shown.Texts);
        Assert.Contains("OADM Site Node", shown.Texts); // the title bar

        switch (state)
        {
            case "open":
                Assert.Contains("Not bound. Binding open for 23 more hours.", shown.Texts);
                Assert.Contains("Pairing code", shown.Texts);
                Assert.Contains("Show pairing code", shown.Texts);
                Assert.Contains("Connected to 10.0.0.5:5081", shown.Texts);
                break;
            case "open-code":
                Assert.Contains("ABCD-EFGH", shown.Texts);
                Assert.Contains("Type this code at Bind on the Sites page of OADM.", shown.Texts);
                Assert.DoesNotContain("Show pairing code", shown.Texts);
                break;
            case "saved-code":
                // Saving the address (as an administrator) brought the code right below it.
                Assert.Contains("ABCD-EFGH", shown.Texts);
                Assert.Contains("Type this code at Bind on the Sites page of OADM.", shown.Texts);
                Assert.DoesNotContain("Show pairing code", shown.Texts);
                Assert.Contains("Not bound. Binding open for 23 more hours.", shown.Texts);
                break;
            case "server":
                Assert.Contains("Server address", shown.Texts);
                Assert.Contains(NodeAppViewModel.EnterServer + " " + NodeAppViewModel.PortHint, shown.Texts);
                Assert.Contains("No server address yet", shown.Texts);
                Assert.DoesNotContain("Show pairing code", shown.Texts); // the address comes first
                break;
            case "cannot-reach":
                Assert.Contains("Cannot reach 10.0.0.5:5081", shown.Texts); // below the address field
                Assert.Contains("Not connected: Cannot reach 10.0.0.5:5081", shown.Texts);
                break;
            case "closed":
                Assert.Contains("Binding is closed.", shown.Texts);
                Assert.Contains("Allow binding", shown.Texts);
                Assert.DoesNotContain("Show pairing code", shown.Texts);
                break;
            case "bound":
                Assert.Contains("Connected to OADM-SRV", shown.Texts);
                Assert.Contains("Site Building B", shown.Texts);
                Assert.Contains(shown.Texts, t => t.StartsWith("Bound since ", StringComparison.Ordinal));
                Assert.Single(shown.Texts, t => t.Contains("Building B", StringComparison.Ordinal)); // each name once
                Assert.Single(shown.Texts, t => t.Contains("OADM-SRV", StringComparison.Ordinal));
                Assert.DoesNotContain("Show pairing code", shown.Texts);
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
                Assert.DoesNotContain("Networks", shown.Texts); // no tabs without the service
                break;
        }

        if (state != "no-service")
        {
            Assert.Contains("Networks", shown.Texts);
            Assert.Contains("Server address", shown.Texts);
            Assert.Contains("Node ID a1b2c3d4… · Version 3.7.0", shown.Texts); // small and grey in the Node status block
            Assert.DoesNotContain("Node ID", shown.Texts); // no longer a row of General
            Assert.Contains("Reset", shown.Texts);
            Assert.DoesNotContain("Wait for the server", shown.Texts); // the node always connects to the server
            Assert.DoesNotContain("Connection log", shown.Texts); // the other tabs stay hidden
            Assert.DoesNotContain("Review", shown.Texts); // no requests in these states
        }
    }

    [Theory]
    [InlineData(NodeTab.General, "general")]
    [InlineData(NodeTab.Networks, "networks")]
    [InlineData(NodeTab.Ports, "ports")]
    [InlineData(NodeTab.Activity, "activity")]
    public async Task EachTabShowsItsPart(NodeTab tab, string name)
    {
        var shown = await ShowAsync(FakeChannel.Sample(), tab, $"site-node-app-tab-{name}.png", showCode: true);

        Assert.Equal(["Save"], shown.Primary);
        Assert.Contains("The server asks for 1 port.", shown.Texts);
        Assert.Contains("Review", shown.Texts);
        Assert.Contains("1", shown.Texts); // the count badge of the Ports tab
        Assert.Contains("Connected to 10.0.0.5:5081", shown.Texts);
        switch (tab)
        {
            case NodeTab.General:
                Assert.Contains("Server address", shown.Texts);
                Assert.Contains("Node ID a1b2c3d4… · Version 3.7.0", shown.Texts);
                Assert.Contains("Reset", shown.Texts);
                Assert.DoesNotContain("All cards", shown.Texts);
                break;
            case NodeTab.Networks:
                Assert.Contains("Reach devices on", shown.Texts);
                Assert.Contains("Cameras", shown.Texts);
                Assert.Contains("All cards", shown.Texts);
                Assert.Contains("Default router", shown.Texts);
                Assert.Contains("10.20.0.0/16", shown.Texts);
                Assert.DoesNotContain("Reset", shown.Texts);
                break;
            case NodeTab.Ports:
                Assert.Contains("Connect to TCP 8443: camera 192.168.0.30", shown.Texts);
                Assert.Contains("Allow", shown.Texts);
                Assert.Contains("Listen on", shown.Texts);
                break;
            case NodeTab.Activity:
                Assert.Contains("3 short, 12 permanent", shown.Texts);
                Assert.Contains("NTP server, UDP 123", shown.Texts);
                Assert.Contains("Connection log", shown.Texts);
                break;
        }
    }

    [Fact]
    public async Task TheNetworksTabWithAllCards()
    {
        var shown = await ShowAsync(FakeChannel.Sample(requests: false, allCards: true), NodeTab.Networks, "site-node-app-tab-networks-all.png", showCode: false);

        Assert.Contains("All cards", shown.Texts);
        Assert.Contains("Office", shown.Texts);
        Assert.Contains("10.20.0.0/16", shown.Texts);
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
        "open" or "open-code" => FakeChannel.Sample(requests: false),
        "saved-code" => FakeChannel.Sample(serverAddress: "", connected: false, connectionText: "", requests: false),
        "server" => FakeChannel.Sample(serverAddress: "", connected: false, connectionText: "", requests: false),
        "cannot-reach" => FakeChannel.Sample(connected: false, connectionText: "Cannot reach 10.0.0.5:5081", requests: false),
        "closed" => FakeChannel.Sample(NodeBinding.Closed, requests: false),
        "bound" => FakeChannel.Sample(NodeBinding.Bound, requests: false),
        "bound-offline" => FakeChannel.Sample(NodeBinding.Bound, connected: false, connectionText: "Cannot reach 10.0.0.5:5081", requests: false),
        "identity" => FakeChannel.Sample(NodeBinding.Bound, newIdentity: "0123456789ABCDEF0123456789ABCDEF", requests: false),
        _ => null,
    };

    /// <summary>The window as it opens (its own size) on <paramref name="tab"/>: the texts it shows and its accent buttons.</summary>
    private static Task<(List<string> Texts, List<string> Primary)> ShowAsync(NodeView? state, NodeTab tab, string screenshot, bool showCode, string? saveAddress = null) =>
        HeadlessEntry.Session.Dispatch(async () =>
        {
            var channel = new FakeChannel { State = state };
            var vm = new NodeAppViewModel(channel, new FakeElevation(channel) { IsAdministrator = true }, new FakeDialogs());
            await vm.RefreshAsync();
            await vm.LoadLogAsync();
            if (saveAddress is not null)
            {
                vm.ServerAddress = saveAddress;
                await vm.SaveServerAsync();
            }

            if (showCode)
                await vm.ShowPairingCodeAsync();
            vm.Tab = tab;
            var window = new MainWindow { DataContext = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Save(window, screenshot);
            var texts = window.GetVisualDescendantsTexts();
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
