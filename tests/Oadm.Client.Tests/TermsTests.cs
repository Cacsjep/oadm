using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

using Oadm.Client.Infrastructure;
using Oadm.Client.Shell;

namespace Oadm.Client.Tests;

/// <summary>The terms of use on the first start: Accept stores the version of the text, Quit and the X do not.</summary>
public sealed class TermsTests
{
    [Fact]
    public void Accept_stores_the_version_and_changed_terms_ask_again()
    {
        var settings = new InMemoryClientSettingsStore();
        Assert.False(TermsViewModel.IsAccepted(settings.Current));
        var vm = new TermsViewModel(settings);
        bool? result = null;
        vm.Completed += (_, accepted) => result = accepted;

        vm.AcceptCommand.Execute(null);

        Assert.True(result);
        Assert.True(TermsViewModel.IsAccepted(settings.Current));
        Assert.Equal(64, settings.Current.AcceptedTerms.Length);
        Assert.StartsWith("Terms of use", vm.Title, StringComparison.Ordinal);
        Assert.NotEmpty(vm.Text);
        Assert.Contains("Apache License", vm.License, StringComparison.Ordinal);

        settings.Current.AcceptedTerms = TermsViewModel.Version("older terms");
        Assert.False(TermsViewModel.IsAccepted(settings.Current));
        Assert.Equal(TermsViewModel.Version("a\r\nb"), TermsViewModel.Version("a\nb"));
    }

    [Fact]
    public void Quit_stores_nothing()
    {
        var settings = new InMemoryClientSettingsStore();
        var vm = new TermsViewModel(settings);
        bool? result = null;
        vm.Completed += (_, accepted) => result = accepted;

        vm.QuitCommand.Execute(null);

        Assert.False(result);
        Assert.Empty(settings.Current.AcceptedTerms);
    }

    [Fact]
    public async Task The_window_renders_and_the_x_counts_as_quit()
    {
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        var results = await HeadlessSession.Shared.Dispatch(() =>
        {
            var settings = new InMemoryClientSettingsStore();
            var window = new TermsWindow();
            window.Attach(new TermsViewModel(settings));
            var completed = new List<bool>();
            window.Completed += (_, accepted) => completed.Add(accepted);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, "client-terms.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            window.Close();
            return Task.FromResult(completed);
        }, CancellationToken.None);

        Assert.Equal([false], results);
    }
}
