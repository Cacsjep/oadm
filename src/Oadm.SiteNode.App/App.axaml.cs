using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.App.Services;
using Oadm.SiteNode.App.ViewModels;
using Oadm.SiteNode.App.Views;
using Oadm.Theme;
using Oadm.Theme.Shell;

namespace Oadm.SiteNode.App;

public partial class App : Application
{
    /// <summary>Set by Program before the app starts.</summary>
    public static AppStart Start { get; set; } = new(AdminEndpoint.Default, null, AppSettingsStore.DefaultFolder);

    /// <summary>Tests choose the appearance; the app follows the OS.</summary>
    public static Appearance? AppearanceOverride { get; set; }

    public override void Initialize()
    {
        // The app has no settings page: it follows the OS's dark or light setting at the start.
        OadmColors.Current = AppearanceOverride ?? (PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Light ? Appearance.Light : Appearance.Dark);
        AvaloniaXamlLoader.Load(this);
        RequestedThemeVariant = OadmColors.VariantOf(OadmColors.Current);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) =>
        {
            TextOptions.SetTextRenderingMode(window, TextRenderingMode.SubpixelAntialias);
            TextOptions.SetTextHintingMode(window, TextHintingMode.Strong);
            TextOptions.SetBaselinePixelAlignment(window, BaselinePixelAlignment.Aligned);
            window.Icon ??= Icon.Value;
        });
        WindowFit.Attach(); // a small or scaled screen: the window shrinks to fit

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = new AppSettingsStore(Start.SettingsFolder);
            var current = settings.Load();
            if (NodeTermsViewModel.IsAccepted(current))
            {
                ShowMain(desktop);
            }
            else
            {
                // The terms first: Quit (or the X) ends the app.
                var terms = new TermsWindow();
                terms.Attach(new NodeTermsViewModel(settings, current));
                terms.Completed += (_, accepted) =>
                {
                    if (accepted)
                    {
                        ShowMain(desktop);
                        terms.Close();
                    }
                    else
                    {
                        desktop.Shutdown();
                    }
                };
                desktop.MainWindow = terms;
                terms.Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ShowMain(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var window = new MainWindow();
        var viewModel = new NodeAppViewModel(new AdminChannel(Start.Endpoint), new SystemElevation(Start.DataDirectory), new WindowDialogs(window));
        window.Attach(viewModel);
        desktop.MainWindow = window;
        window.Show();
    }

    private static readonly Lazy<WindowIcon?> Icon = new(() =>
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://oadm-site-node-app/Assets/" + (OperatingSystem.IsWindows() ? "oadm.ico" : "oadm-256.png")));
            return new WindowIcon(stream);
        }
#pragma warning disable CA1031 // A missing icon never stops a window from opening.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    });
}

/// <summary>Where the app finds its service and keeps its settings.</summary>
public sealed record AppStart(AdminEndpoint Endpoint, string? DataDirectory, string SettingsFolder);

/// <summary>The terms of use on the first start of the app on this computer (the accepted version in the app's settings).</summary>
public sealed class NodeTermsViewModel(AppSettingsStore store, AppSettings settings)
    : TermsDialogViewModel(Terms.Title, Terms.Body, TermsText.Resource(typeof(NodeTermsViewModel).Assembly, "Oadm.SiteNode.App.LICENSE"))
{
    public static (string Title, string Body) Terms { get; } = TermsText.Parse(TermsText.Resource(typeof(NodeTermsViewModel).Assembly, "Oadm.SiteNode.App.TERMS.md"));

    public static string CurrentVersion { get; } = TermsText.Version(Terms.Title + "\n" + Terms.Body);

    public static bool IsAccepted(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return string.Equals(settings.AcceptedTerms, CurrentVersion, StringComparison.Ordinal);
    }

    protected override void OnAccepted()
    {
        settings.AcceptedTerms = CurrentVersion;
        store.Save(settings);
    }
}

/// <summary>Confirmations in the shared message window.</summary>
public sealed class WindowDialogs(Window owner) : IAppDialogs
{
    public Task<bool> ConfirmAsync(string title, string message, string confirmText) =>
        Sdk.Client.Controls.MessageWindow.ConfirmAsync(owner, title, message, confirmText);
}
