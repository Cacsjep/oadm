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
    [Theory]
    [InlineData(NodeBinding.Open, "site-node-app-open.png")]
    [InlineData(NodeBinding.Bound, "site-node-app-bound.png")]
    [InlineData(NodeBinding.Closed, "site-node-app-closed.png")]
    public async Task TheWindowShowsEveryPart(NodeBinding binding, string screenshot)
    {
        var texts = await HeadlessEntry.Session.Dispatch(async () =>
        {
            var channel = new FakeChannel { State = FakeChannel.Sample(binding) };
            var vm = new NodeAppViewModel(channel, new FakeElevation(channel) { IsAdministrator = true }, new FakeDialogs());
            await vm.RefreshAsync();
            await vm.LoadLogAsync();
            if (binding == NodeBinding.Open)
                await vm.ShowPairingCodeAsync();
            var window = new MainWindow { DataContext = vm, Width = 1180, Height = 1100 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Save(window, screenshot);
            var shown = window.GetVisualDescendantsTexts();
            window.Close();
            return shown;
        }, CancellationToken.None);

        Assert.Contains("Networks", texts);
        Assert.Contains("Connect to", texts);
        Assert.Contains("Connection log", texts);
        Assert.Contains("Reset", texts);
        if (binding == NodeBinding.Open)
            Assert.Contains("ABCD-EFGH", texts);
        if (binding == NodeBinding.Closed)
            Assert.Contains("Allow binding", texts);
        if (binding == NodeBinding.Bound)
            Assert.Contains("Building B", texts);
    }

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
