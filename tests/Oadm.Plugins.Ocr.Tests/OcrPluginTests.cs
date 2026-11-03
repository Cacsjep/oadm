using System.Text.Json;

using Oadm.Core.Plugins;
using Oadm.Core.Plugins.AddOns;
using Oadm.Plugins.Ocr.Api;
using Oadm.Plugins.Ocr.Model;
using Oadm.Sdk.Plugins;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.Ocr.Tests;

/// <summary>The skeleton of Text recognition (docs/spec/ocr.md "Build plan", wave 1): manifest, loading, limits, payloads.</summary>
public sealed class OcrPluginTests
{
    [Fact]
    public void TheManifestIsAnAddOnOffByDefault()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(TestRepository.Path("plugins", "Oadm.Plugins.Ocr", "plugin.json")));
        var root = manifest.RootElement;

        Assert.Equal(OcrInfo.PluginId, root.GetProperty("id").GetString());
        Assert.Equal("Text recognition", root.GetProperty("displayName").GetString());
        Assert.False(root.GetProperty("enabledByDefault").GetBoolean());
        Assert.Equal(SdkInfo.Version, root.GetProperty("minSdkVersion").GetString());
        Assert.Equal(OcrInfo.Description, root.GetProperty("description").GetString());

        // The add-on catalogue offers it with the same id, name and line.
        var entry = AddOnCatalog.Find(OcrInfo.PluginId);
        Assert.NotNull(entry);
        Assert.Equal(OcrInfo.DisplayName, entry.DisplayName);
        Assert.Equal(OcrInfo.Description, entry.Description);

        // Both projects are add-ons: left out of the installers by the publish scripts.
        foreach (var project in new[] { Path.Combine("Oadm.Plugins.Ocr", "Oadm.Plugins.Ocr.csproj"), Path.Combine("Oadm.Plugins.Ocr.Client", "Oadm.Plugins.Ocr.Client.csproj") })
        {
            Assert.Contains("<OadmAddOn>true</OadmAddOn>", File.ReadAllText(TestRepository.Path("plugins", project)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePluginIsAVisionPageWithItsPermissions()
    {
        var plugin = new OcrPlugin();

        Assert.Equal("oadm.ocr", plugin.Id);
        Assert.Equal("Text recognition", plugin.DisplayName);
        Assert.Equal(CorePluginGroup.Vision, plugin.Group);
        Assert.True(((ICorePlugin)plugin).HasPage);
        Assert.Equal([PermissionKeys.Show, OcrInfo.ChangePermission], plugin.Permissions.Select(p => p.Key));
        Assert.Equal("Change text readers", plugin.Permissions[1].Name);
        Assert.Equal(OcrInfo.ChangePermission, plugin.RequiredPermission(OcrMethods.SaveReader));
        Assert.Equal(OcrInfo.ChangePermission, plugin.RequiredPermission(OcrMethods.DeleteReader));
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(OcrMethods.StartPreview));
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(OcrMethods.StopPreview));
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(OcrMethods.GetPreview));
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(OcrMethods.ListReaders));
        Assert.Equal(PermissionKeys.Show, plugin.RequiredPermission(OcrMethods.GetReading));
    }

    [Fact]
    public void ThePackageLoadsFromItsFolderOff()
    {
        // The add-on's way in (docs/spec/add-ons.md "Server"): the plugin folder loaded on its own, its own load context.
        var folder = Path.Combine(Path.GetTempPath(), "oadm-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var registry = new PluginRegistry();
        var loader = new PluginLoader(registry);
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Oadm.Plugins.Ocr.Server.dll"), Path.Combine(folder, "Oadm.Plugins.Ocr.Server.dll"));
            File.Copy(TestRepository.Path("plugins", "Oadm.Plugins.Ocr", "plugin.json"), Path.Combine(folder, "plugin.json"));

            var package = loader.LoadPackage(folder);

            Assert.True(registry.Errors.Count == 0, string.Join("; ", registry.Errors.Select(e => e.Message)));
            Assert.NotNull(package);
            var core = Assert.Single(registry.AllCorePlugins);
            Assert.Equal(OcrInfo.PluginId, core.Id);
            Assert.Equal(CorePluginGroup.Vision, core.Plugin.Group);
            var state = Assert.Single(registry.Packages);
            Assert.False(state.Origin.EnabledByDefault);
            Assert.False(state.Enabled);
            Assert.Empty(registry.CorePlugins); // off: no page until an administrator turns it on
        }
        finally
        {
            foreach (var package in loader.Packages)
            {
                package.LoadContext.Unload();
            }

            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The assembly may stay mapped until the load context is collected; the temp folder is unique.
            }
        }
    }

    [Fact]
    public void TheLimitsAreThoseOfTheSpec()
    {
        Assert.Equal(8, OcrLimits.MaxAreas);
        Assert.Equal(50, OcrLimits.MaxReaders);
        Assert.Equal(20, OcrLimits.MaxWatching);
        Assert.Equal([1, 2, 4], OcrLimits.FpsChoices);
        Assert.Equal(1, OcrLimits.DefaultFps);
        Assert.Equal(50, OcrLimits.MinConfidence);
        Assert.Equal(2, OcrLimits.EqualFramesToPublish);
        Assert.Equal(TimeSpan.FromMinutes(1), OcrLimits.MaxReopenPause);
        Assert.Equal(1, OcrLimits.Workers(1));
        Assert.Equal(1, OcrLimits.Workers(2));
        Assert.Equal(4, OcrLimits.Workers(8));
        Assert.Equal(new Resolution(1920, 1080), new Reader().Resolution);
        Assert.Equal("1920x1080", Resolution.Default.ToString());
        Assert.Equal(ReaderMode.Watching, new Reader().Mode); // new readers start watching
        Assert.Equal(ExpectedTextKind.Any, new Area().Expected.Kind);
    }

    [Fact]
    public void TheEventAndActionIdsAreStable()
    {
        Assert.Equal("oadm.ocr.text-read.r1", OcrInfo.TextReadTypeId("r1"));
        Assert.Equal(["Text recognition", "Gate 2"], OcrInfo.TextReadPath("Gate 2"));
        Assert.Equal("oadm.ocr.read-text", OcrInfo.ReadTextActionId);
        Assert.Equal("Read text", OcrInfo.ReadTextActionName);
        Assert.Equal("Text read", OcrInfo.TextReadName);
    }

    [Fact]
    public void ARelativeBoxStaysInsideThePicture()
    {
        Assert.True(RelativeBox.Whole.IsValid);
        Assert.True(new RelativeBox(0.5, 0.25, 0.5, 0.75).IsValid);
        Assert.False(new RelativeBox(0.6, 0, 0.5, 1).IsValid);
        Assert.False(new RelativeBox(0, 0, 0, 1).IsValid);
        Assert.False(new RelativeBox(-0.1, 0, 0.5, 0.5).IsValid);
    }

    [Fact]
    public void TheSaveRequestRoundTripsAsJson()
    {
        var reader = new Reader
        {
            Id = "r1",
            Name = "Gate 2",
            DeviceId = Guid.Parse("6f0c2a51-0b8e-4d35-9b5e-0d2b9b3c1a77"),
            VideoSource = 2,
            Resolution = new Resolution(1280, 720),
            Mode = ReaderMode.Watching,
            Fps = 4,
            Areas =
            [
                new Area { Name = "Container number", Box = new RelativeBox(0.1, 0.2, 0.3, 0.1), Expected = ExpectedText.WithFormat("AAAU ###### #") },
                new Area { Name = "All" },
            ],
        };

        var json = OcrJson.Serialize(new SaveReaderRequest { Reader = reader });
        var back = OcrJson.Deserialize<SaveReaderRequest>(json).Reader;

        Assert.Contains("\"mode\":\"Watching\"", json, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"Format\"", json, StringComparison.Ordinal);
        Assert.Equal(reader with { Areas = back.Areas }, back);
        Assert.Equal(reader.Areas, back.Areas);
        Assert.Equal(json, OcrJson.Serialize(new SaveReaderRequest { Reader = back }));
    }

    [Fact]
    public void AReadingWithItsPictureRoundTripsAsJson()
    {
        var reply = new ReadingReply
        {
            Reading = new Reading
            {
                ReaderId = "r1",
                Time = new DateTimeOffset(2026, 11, 3, 10, 15, 0, TimeSpan.Zero),
                Resolution = new Resolution(1920, 1080),
                Areas =
                [
                    new AreaResult
                    {
                        Area = "Container number",
                        Text = "MSCU 123456 7",
                        TextRead = "MSCU 123456 7\nMAX GROSS",
                        Confidence = 91.5,
                        MatchesFormat = true,
                        Lines = [new TextLine("MSCU 123456 7", new RelativeBox(0.1, 0.2, 0.3, 0.05), 91.5), new TextLine("MAX GROSS", new RelativeBox(0.1, 0.3, 0.2, 0.04), 77)],
                    },
                ],
            },
            Jpeg = [0xFF, 0xD8, 0xFF, 0xD9],
        };

        var json = OcrJson.Serialize(reply);
        var back = OcrJson.Deserialize<ReadingReply>(json);

        Assert.Equal(reply.Jpeg, back.Jpeg);
        Assert.Equal(json, OcrJson.Serialize(back));
        Assert.Equal(reply.Reading!.Areas[0].Lines, back.Reading!.Areas[0].Lines);

        var list = new ReaderList { Readers = [new ReaderSummary { Id = "r1", Name = "Gate 2", Mode = ReaderMode.OnAction, LastText = "MSCU 123456 7" }] };
        Assert.Equal(OcrJson.Serialize(list), OcrJson.Serialize(OcrJson.Deserialize<ReaderList>(OcrJson.Serialize(list))));
    }
}
