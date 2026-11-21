using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.Client.Tests;

/// <summary>The shared dialog controls of Oadm.Sdk.Client, rendered with the host theme.</summary>
public sealed class SharedDialogControlsTests
{
    [Fact]
    public async Task Dialog_controls_take_their_look_from_the_theme()
    {
        HeadlessUnitTestSession session = HeadlessSession.Shared;
        await session.Dispatch(() =>
        {
            var cancelled = false;
            var title = new DialogTitleBar { Text = "Upgrade firmware" };
            var header = new CardHeader { Title = "Firmware file" };
            var chip = new StatusChip { Text = "Upgrade", IsOk = true };
            var progress = new ProgressRow { Value = 42, Text = "Uploading", IsActive = false };
            var file = new FileRow { FileName = "P3265-V_12_11_77.bin", Details = "87.0 MB" };
            var footer = new DialogFooter { CancelCommand = new RelayCommand(() => cancelled = true) };
            footer.Actions.Add(new Button { Content = "Apply" });
            var window = new Window
            {
                Width = 600,
                Height = 400,
                Content = new StackPanel { Children = { title, header, chip, progress, file, footer } },
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            // Dialog header: the title as a heading and an X that closes the window (no system title bar, does not move).
            // The title and its optional grey side line sit in one row; without a Subtitle the line is hidden.
            var titleGrid = Assert.IsType<Grid>(title.Child);
            TextBlock[] titleTexts = [.. titleGrid.Children.OfType<StackPanel>().First(p => Grid.GetColumn(p) == 0).Children.OfType<TextBlock>()];
            Assert.Contains("cardTitle", titleTexts[0].Classes);
            Assert.Contains("pageSubtitle", titleTexts[1].Classes);
            Assert.False(titleTexts[1].IsVisible);
            title.Subtitle = "Axis devices found on the local network.";
            Assert.True(titleTexts[1].IsVisible);
            Assert.Equal("Axis devices found on the local network.", titleTexts[1].Text);
            Assert.False(header.Children.OfType<TextBlock>().Single(t => t.Classes.Contains("secondary")).IsVisible);

            // Status = colored icon + plain text, no chip border.
            Assert.Contains("status", chip.Classes);
            Assert.Contains("ok", chip.Classes);
            OadmIcon statusIcon = chip.Children.OfType<OadmIcon>().Single();
            Assert.IsAssignableFrom<Geometry>(statusIcon.Data);
            chip.IsOk = false;
            chip.IsError = true;
            chip.Detail = "Login failed";
            Assert.DoesNotContain("ok", chip.Classes);
            Assert.Contains("error", chip.Classes);
            Assert.Equal("Upgrade: Login failed", chip.Children.OfType<TextBlock>().Single().Text);

            ProgressBar bar = progress.Children.OfType<ProgressBar>().Single();
            Assert.Equal(42, bar.Value);
            Assert.DoesNotContain("stream", bar.Classes);

            Assert.IsAssignableFrom<Geometry>(file.Children.OfType<OadmIcon>().Single().Data);
            Button choose = file.Children.OfType<Button>().Single();
            Assert.Contains("secondary", choose.Classes);
            Assert.IsAssignableFrom<Geometry>(((IconLabel)choose.Content!).Icon);

            Assert.Equal("Cancel", footer.CancelButton.Content);
            Assert.True(footer.CancelButton.IsCancel);
            footer.CancelButton.Command!.Execute(null);
            Assert.True(cancelled);
            titleGrid.Children.OfType<Button>().Single().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.IsVisible);
            return Task.CompletedTask;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(87L * 1024 * 1024, "87.0 MB")]
    [InlineData(412L * 1024, "412 KB")]
    public void File_sizes_use_one_format(long bytes, string expected) => Assert.Equal(expected, FileSizeText.Format(bytes));
}
