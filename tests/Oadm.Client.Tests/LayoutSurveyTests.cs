using System.Globalization;
using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.Extensions.DependencyInjection;

using Oadm.Client.Plugins;
using Oadm.Client.Shell;

namespace Oadm.Client.Tests;

/// <summary>
/// Layout survey on request (<c>OADM_LAYOUT_SURVEY=1</c>, not part of any test run): every page of the main window with
/// every fake-mode plugin on, at a small window (<c>OADM_LAYOUT_SURVEY_SIZE</c>, default 1536x816: a Full HD screen at
/// 125 % scaling minus the taskbar), with a screenshot each and a list of the controls that run out of the window or are
/// cut off by a parent that does not scroll (<c>layout-survey.txt</c> in <c>OADM_SCREENSHOT_DIR</c>).
/// </summary>
public sealed class LayoutSurveyTests
{
    [Fact]
    public async Task Every_page_fits_a_small_window()
    {
        if (Environment.GetEnvironmentVariable("OADM_LAYOUT_SURVEY") != "1")
        {
            return; // on request only
        }

        var size = (Environment.GetEnvironmentVariable("OADM_LAYOUT_SURVEY_SIZE") ?? "1536x816").Split('x');
        var (width, height) = (int.Parse(size[0], CultureInfo.InvariantCulture), int.Parse(size[1], CultureInfo.InvariantCulture));
        var outDir = Environment.GetEnvironmentVariable("OADM_SCREENSHOT_DIR") ?? Path.Combine(Path.GetTempPath(), "oadm-layout-survey");
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();

        await HeadlessSession.Shared.Dispatch(async () =>
        {
            var app = (App)Application.Current!;
            var vm = app.Services!.GetRequiredService<MainWindowViewModel>();
            var packages = app.Services!.GetRequiredService<PluginPackageStore>();
            var window = new MainWindow { DataContext = vm, MinWidth = 0, MinHeight = 0, Width = width, Height = height };
            window.Show();
            vm.Start();
            await PumpAsync(() => vm.Devices.FilteredDevices.Count > 0);
            await packages.LoadAsync(CancellationToken.None);
            foreach (var package in packages.Packages.Where(p => !p.Enabled && !p.AlwaysOn).ToList())
            {
                await packages.SetEnabledAsync(package.Id, true, CancellationToken.None);
            }

            await PumpAsync(() => true, 1500);
            foreach (var expanded in new[] { true, false })
            {
                vm.IsNavExpanded = expanded;
                foreach (var item in vm.NavItems.Concat(vm.BottomNavItems).ToList())
                {
                    vm.NavigateCommand.Execute(item);
                    await PumpAsync(() => true, 600);
                    var name = $"{width}x{height}-{(expanded ? "rail" : "narrow")}-{item.Key.Replace(':', '-')}";
                    Capture(window, Path.Combine(outDir, name + ".png"));
                    foreach (var problem in Problems(window))
                    {
                        report.AppendLine(CultureInfo.InvariantCulture, $"{name}: {problem}");
                    }
                }
            }

            window.Close();
            return true;
        }, CancellationToken.None);

        await File.WriteAllTextAsync(Path.Combine(outDir, "layout-survey.txt"), report.ToString());
    }

    /// <summary>Controls that end outside the window, or outside a parent that clips without scrolling.</summary>
    private static IEnumerable<string> Problems(Window window)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var control in window.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0))
        {
            if (control.FindAncestorOfType<ScrollContentPresenter>() is not null || control.FindAncestorOfType<DataGrid>() is not null
                || control is ScrollContentPresenter)
            {
                continue; // inside something that scrolls: allowed to be larger
            }

            if (control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), window) is not { } corner)
            {
                continue;
            }

            string? where = null;
            if (corner.X > window.Bounds.Width + 1 || corner.Y > window.Bounds.Height + 1)
            {
                where = $"out of the window by {Math.Max(0, corner.X - window.Bounds.Width):0} x {Math.Max(0, corner.Y - window.Bounds.Height):0} px";
            }
            else if (control.GetVisualAncestors().OfType<Control>().FirstOrDefault(a => a.ClipToBounds) is { } clip
                && control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), clip) is { } inClip
                && (inClip.X > clip.Bounds.Width + 1 || inClip.Y > clip.Bounds.Height + 1))
            {
                where = $"cut off by {clip.GetType().Name}{Classes(clip)} by {Math.Max(0, inClip.X - clip.Bounds.Width):0} x {Math.Max(0, inClip.Y - clip.Bounds.Height):0} px";
            }

            if (where is null)
            {
                continue;
            }

            // Report the outermost control of a run once (its children overflow with it).
            var label = Describe(control);
            if (control.GetVisualAncestors().OfType<Control>().Any(a => seen.Contains(Describe(a))) || !seen.Add(label))
            {
                continue;
            }

            yield return $"{label} {where}";
        }
    }

    private static string Describe(Control control)
    {
        var text = control switch
        {
            TextBlock t => " \"" + t.Text + "\"",
            Oadm.Sdk.Client.Controls.ToolbarButton b => " \"" + b.Text + "\"",
            Button { Content: string s } => " \"" + s + "\"",
            _ => string.Empty,
        };
        var name = string.IsNullOrEmpty(control.Name) ? string.Empty : "#" + control.Name;
        return control.GetType().Name + name + Classes(control) + text + "@" + control.GetHashCode().ToString(CultureInfo.InvariantCulture);
    }

    private static string Classes(Control control) => control.Classes.Count == 0 ? string.Empty : "." + string.Join('.', control.Classes);

    private static async Task PumpAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        do
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
            Dispatcher.UIThread.RunJobs();
        }
        while (!condition() && DateTime.UtcNow < end);
    }

    private static void Capture(Window window, string path)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        frame?.Save(path, PngBitmapEncoderOptions.Default);
        frame?.Dispose();
    }
}
