using System.Text.RegularExpressions;
using System.Xml.Linq;

using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Client.Infrastructure;
using Oadm.Client.Settings;
using Oadm.Client.Shell;
using Oadm.Client.Themes;

namespace Oadm.Client.Tests;

/// <summary>The dark and light colors (Themes/Colors.*.axaml) and the "Appearance" setting.</summary>
public sealed partial class ThemeColorsTests
{
    [Fact]
    public void Both_appearances_define_the_same_colors()
    {
        var dark = ColorKeys("Dark");
        var light = ColorKeys("Light");

        Assert.NotEmpty(dark);
        Assert.Empty(dark.Except(light));
        Assert.Empty(light.Except(dark));
    }

    [Fact]
    public void Every_color_the_theme_uses_is_defined()
    {
        string theme = File.ReadAllText(Path.Combine(ThemesFolder(), "OadmTheme.axaml"));
        var used = ColorReference().Matches(theme).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var defined = ColorKeys("Dark");

        Assert.NotEmpty(used);
        Assert.Empty(used.Except(defined));
        Assert.DoesNotContain("<Color x:Key=", theme, StringComparison.Ordinal); // colors live only in the appearance files
    }

    [Fact]
    public void The_appearance_is_read_from_the_settings_file_before_the_start()
    {
        string folder = Path.Combine(Path.GetTempPath(), "oadm-appearance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Equal(Appearance.Dark, JsonClientSettingsStore.ReadAppearance(folder)); // no file

            var store = new JsonClientSettingsStore(new AppOptions { DataFolder = folder }, NullLogger<JsonClientSettingsStore>.Instance);
            store.Current.Appearance = Appearance.Light;
            store.Save();
            Assert.Contains("\"Appearance\": \"Light\"", File.ReadAllText(Path.Combine(folder, "client-settings.json")), StringComparison.Ordinal);
            Assert.Equal(Appearance.Light, JsonClientSettingsStore.ReadAppearance(folder));

            File.WriteAllText(Path.Combine(folder, "client-settings.json"), "{ not json");
            Assert.Equal(Appearance.Dark, JsonClientSettingsStore.ReadAppearance(folder));

            // A number or a later name reads as Dark and keeps the other settings.
            foreach (var value in new[] { "7", "\"Blue\"", "\"1\"" })
            {
                File.WriteAllText(Path.Combine(folder, "client-settings.json"), "{ \"ServerAddress\": \"cam:5080\", \"Appearance\": " + value + " }");
                Assert.Equal(Appearance.Dark, JsonClientSettingsStore.ReadAppearance(folder));
                Assert.Equal("cam:5080", new JsonClientSettingsStore(new AppOptions { DataFolder = folder }, NullLogger<JsonClientSettingsStore>.Instance).Current.ServerAddress);
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Choosing_an_appearance_saves_it_at_once_and_says_it_needs_a_restart()
    {
        var api = new Oadm.Client.Api.FakeOadmApi(TimeSpan.FromMilliseconds(5));
        using var f = new DevicesFixture(api);
        using var connection = new ServerConnection(f.Api, f.Store, f.Tasks, f.Ui, NullLogger<ServerConnection>.Instance);
        Appearance running = OadmColors.Current;
        var store = new InMemoryClientSettingsStore();
        store.Current.Appearance = running; // the file the running client read
        var settings = new SettingsViewModel(f.Api, connection, NullLogger<SettingsViewModel>.Instance, clientSettings: store);
        Appearance other = running == Appearance.Dark ? Appearance.Light : Appearance.Dark;

        Assert.Null(settings.AppearanceHint);
        int saves = store.SaveCount;

        settings.Appearance = other;
        Assert.Equal(other, store.Current.Appearance);
        Assert.Equal(saves + 1, store.SaveCount);
        Assert.Equal(SettingsViewModel.AppearanceRestartHint, settings.AppearanceHint);

        settings.Appearance = running;
        Assert.Null(settings.AppearanceHint);
    }

    private static HashSet<string> ColorKeys(string appearance) =>
        XDocument.Load(Path.Combine(ThemesFolder(), $"Colors.{appearance}.axaml"))
            .Descendants()
            .Where(e => e.Name.LocalName == "Color")
            .Select(e => (string)e.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml"))!)
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"\{StaticResource (Oadm\.[A-Za-z.]+Color)\}")]
    private static partial Regex ColorReference();

    private static string ThemesFolder() => Oadm.Tests.Shared.TestRepository.Path("src", "Oadm.Client", "Themes");
}
