using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Oadm.Client.Discovery;

namespace Oadm.Client.Tests;

/// <summary>"Import from file" shows the columns and an example before the file picker.</summary>
public sealed class ImportHelpTests
{
    [Fact]
    public void The_example_imports_without_a_problem()
    {
        DeviceImportFile file = DeviceImportFile.Parse("devices.csv", DeviceImportFile.Example);
        Assert.Equal(4, file.Lines.Count);
        Assert.All(file.Lines, l => Assert.Null(l.Problem));
    }

    [Fact]
    public void Choose_file_and_Cancel_close_with_their_answer()
    {
        var help = ImportHelpViewModel.ForFile();
        var answers = new List<bool>();
        help.CloseRequested += (_, choose) => answers.Add(choose);
        help.ChooseCommand.Execute(null);
        help.CancelCommand.Execute(null);
        Assert.Equal([true, false], answers);
    }

    [Fact]
    public async Task The_dialog_shows_the_columns_and_the_example()
    {
        string? outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR");
        bool done = await HeadlessSession.Shared.Dispatch(() =>
        {
            var window = new ImportHelpWindow();
            window.Attach(ImportHelpViewModel.ForFile());
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            Assert.Contains("Address", texts);
            Assert.Contains("User name, Password", texts);
            Assert.Contains("Tags", texts);
            Assert.Contains(window.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.IconLabel>(), l => l.Text == "Choose file");
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
                frame.Save(Path.Combine(outDir, "client-import-help.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            window.Close();

            // Import from ADM: no example, the warning about the passwords in the export.
            var adm = new ImportHelpWindow();
            adm.Attach(ImportHelpViewModel.ForAdm());
            adm.Show();
            Dispatcher.UIThread.RunJobs();
            var admTexts = adm.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
            Assert.Contains("Addresses", admTexts);
            Assert.DoesNotContain("Example", admTexts);
            Assert.Contains(adm.GetVisualDescendants().OfType<Oadm.Sdk.Client.Controls.StatusChip>(), c => c.IsEffectivelyVisible && c.IsWarning);
            WriteableBitmap? admFrame = adm.CaptureRenderedFrame();
            if (!string.IsNullOrEmpty(outDir) && admFrame is not null)
            {
                admFrame.Save(Path.Combine(outDir, "client-import-adm-help.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }

            adm.Close();
            return Task.FromResult(true);
        }, CancellationToken.None);
        Assert.True(done);
    }
}
