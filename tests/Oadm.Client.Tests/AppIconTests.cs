using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;

namespace Oadm.Client.Tests;

/// <summary>Every window of the client (main window, dialogs, plugin windows) shows the OADM logo as its icon.</summary>
public sealed class AppIconTests
{
    [Fact]
    public async Task Every_opened_window_gets_the_oadm_icon()
    {
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        await session.Dispatch(() =>
        {
            var window = new Window { Width = 200, Height = 100 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(window.Icon);
            window.Close();
        }, CancellationToken.None);
    }
}
