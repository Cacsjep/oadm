using System.Text.Json;

using Oadm.Core.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.VapixCommander.Tests;

public sealed class LibraryTests
{
    [Fact]
    public void Broken_files_and_invalid_commands_are_reported_and_skipped()
    {
        var good = File.ReadAllText(Path.Combine(Samples.Directory, "Common.json"));
        var library = CommandLibrary.Load(
        [
            ("Common.json", () => good),
            ("Broken.json", () => "{ nope"),
            ("Wrong.json", () => """{ "formatVersion": 2, "category": "Video", "commands": [] }"""),
            ("Video.json", () => """
                { "formatVersion": 1, "category": "Video", "commands": [
                  { "id": "common.brand.read", "version": 1, "name": "Duplicate", "category": "Video", "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ],
                    "writes": false, "fields": [], "request": { "method": "GET", "path": "/x" }, "response": { "kind": "raw" } },
                  { "id": "video.bad", "version": 1, "name": "Bad", "category": "Video", "requires": [],
                    "writes": false, "fields": [], "request": { "method": "GET", "path": "/x" }, "response": { "kind": "raw" } },
                  { "id": "video.ok", "version": 1, "name": "Fine command", "category": "Video", "requires": [ { "api": "param-cgi", "minVersion": "1.0" } ],
                    "writes": false, "fields": [], "request": { "method": "GET", "path": "/x" }, "response": { "kind": "raw" } }
                ] }
                """),
        ]);

        Assert.Equal(["common.basicdeviceinfo.read", "common.brand.read", "common.daynight.shiftlevel", "video.ok"], library.Commands.Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.Equal("Common", library.Commands[0].Category);
        Assert.Equal("video.ok", library.Commands[^1].Id);
        Assert.Equal(4, library.Problems.Count);
        Assert.Contains(library.Problems, p => p.StartsWith("Broken.json", StringComparison.Ordinal));
        Assert.Contains(library.Problems, p => p.Contains("duplicate id", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_folder_is_an_empty_library()
    {
        Assert.Empty(CommandLibrary.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).Commands);
    }
}

public sealed class CompatibilityTests
{
    [Fact]
    public void Requires_is_checked_per_major_and_minor_version()
    {
        var command = Samples.ShiftLevel;
        command.Requires = [new ApiRequirement { Api = "daynight", MinVersion = "1.1" }];

        Assert.Equal(CompatibilityState.Compatible, Compatibility.Check(command, [new DeviceApi("daynight", "1.2")]).State);
        Assert.Equal("Version too old: daynight 1.0, needs 1.1", Compatibility.Check(command, [new DeviceApi("daynight", "1.0")]).Text);
        Assert.Equal("Has daynight 2.0, needs 1.1", Compatibility.Check(command, [new DeviceApi("daynight", "2.0")]).Text);
        Assert.Equal("Missing API daynight", Compatibility.Check(command, [new DeviceApi("param-cgi", "1.0")]).Text);
        Assert.Equal(CompatibilityState.Unknown, Compatibility.Check(command, []).State);
        command.HasVideoOnly = true;
        Assert.Equal(CompatibilityState.NoVideo, Compatibility.Check(command, [new DeviceApi("daynight", "1.2")], hasVideo: false).State);
        Assert.Throws<DeviceNotCompatibleException>(() => Compatibility.Require(command, [new DeviceApi("daynight", "1.0")]));
    }
}

public sealed class SavedCommandStoreTests
{
    private readonly InMemoryPluginSettingsProvider _settings = new();

    private SavedCommandStore Store(bool secrets = true) =>
        new(_settings.GetSettings(VapixCommanderPlugin.PluginId), secrets ? TestSecrets.Create() : null);

    private static CommandDefinition WithPassword(string? value)
    {
        var command = Samples.Parse("""
            { "id": "", "version": 1, "name": "Set root password", "category": "Users", "requires": [ { "api": "user-management", "minVersion": "1.0" } ],
              "writes": true, "dangerous": true,
              "fields": [ { "name": "user", "label": "User", "type": "string", "default": "root" }, { "name": "pwd", "label": "Password", "type": "password" } ],
              "request": { "method": "POST", "path": "/axis-cgi/pwdgrp.cgi", "bodyType": "form", "body": { "action": "update", "user": "{{user}}", "pwd": "{{pwd}}" } },
              "response": { "kind": "text", "success": "OK" } }
            """);
        command.Fields[1].Default = value is null ? null : JsonSerializer.SerializeToElement(value);
        return command;
    }

    [Fact]
    public async Task Save_list_update_rename_and_delete()
    {
        var store = Store();
        var preset = Samples.ShiftLevel;
        preset.Id = string.Empty;
        preset.Name = "Day Night Level 50";
        preset.Category = CommandCategories.Custom;

        var saved = await store.SaveAsync(preset, null, "tech@pc", CancellationToken.None);
        Assert.Equal("custom.day-night-level-50", saved.Command.Id);
        Assert.Equal(CommandSources.Saved, saved.Source);
        Assert.Equal("tech@pc", saved.UpdatedBy);

        var second = Samples.ShiftLevel;
        second.Id = string.Empty;
        second.Name = "Day Night Level 50";
        Assert.Equal("custom.day-night-level-50-2", (await store.SaveAsync(second, null, null, CancellationToken.None)).Command.Id);

        var renamed = (await store.FindAsync("custom.day-night-level-50", CancellationToken.None))!;
        renamed.Id = "custom.night-70";
        renamed.Name = "Night 70";
        await store.SaveAsync(renamed, "custom.day-night-level-50", null, CancellationToken.None);

        var list = await store.ListAsync(CancellationToken.None);
        Assert.Equal(["custom.day-night-level-50-2", "custom.night-70"], list.Select(c => c.Command.Id).Order(StringComparer.Ordinal));
        Assert.True(await store.DeleteAsync("custom.night-70", CancellationToken.None));
        Assert.False(await store.DeleteAsync("custom.night-70", CancellationToken.None));
        Assert.Single(await store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_commands_are_refused()
    {
        var command = Samples.ShiftLevel;
        command.Requires.Clear();

        await Assert.ThrowsAsync<CommandValidationException>(() => Store().SaveAsync(command, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Password_values_are_stored_encrypted_never_listed_or_exported()
    {
        var store = Store();
        var saved = await store.SaveAsync(WithPassword("Sup3rSecret!"), null, null, CancellationToken.None);
        var raw = await _settings.GetSettings(VapixCommanderPlugin.PluginId).GetAsync(SavedCommandStore.SettingsKey, CancellationToken.None);

        Assert.DoesNotContain("Sup3rSecret!", raw!, StringComparison.Ordinal);
        Assert.Equal(["pwd"], saved.StoredSecretFields);
        Assert.Null(saved.Command.Fields[1].Default);
        Assert.Equal("Sup3rSecret!", (await store.GetSecretsAsync(saved.Command.Id, CancellationToken.None))["pwd"]);

        // Saving again with an empty password keeps the stored one (also across a rename).
        var edited = (await store.FindAsync(saved.Command.Id, CancellationToken.None))!;
        edited.Id = "custom.root-password";
        await store.SaveAsync(edited, saved.Command.Id, null, CancellationToken.None);
        Assert.Equal("Sup3rSecret!", (await store.GetSecretsAsync("custom.root-password", CancellationToken.None))["pwd"]);

        var export = await store.ExportAsync([], CancellationToken.None);
        Assert.DoesNotContain("Sup3rSecret!", export.Json, StringComparison.Ordinal);
        Assert.Equal("set-root-password.json", export.FileName);
    }

    [Fact]
    public async Task Without_a_protector_passwords_are_dropped()
    {
        var store = Store(secrets: false);
        var saved = await store.SaveAsync(WithPassword("Sup3rSecret!"), null, null, CancellationToken.None);

        Assert.Empty(saved.StoredSecretFields);
        Assert.Empty(await store.GetSecretsAsync(saved.Command.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Export_and_import_round_trip_in_library_format()
    {
        var source = Store();
        var preset = Samples.ShiftLevel;
        preset.Id = "custom.dn50";
        preset.Name = "Day Night Level 50";
        preset.Category = CommandCategories.Custom;
        await source.SaveAsync(preset, null, null, CancellationToken.None);
        await source.SaveAsync(WithPassword(null), null, null, CancellationToken.None);

        var export = await source.ExportAsync([], CancellationToken.None);
        var file = CommandJson.ParseLibraryFile(export.Json);
        Assert.Equal(1, file.FormatVersion);
        Assert.Equal(CommandCategories.Custom, file.Category);
        Assert.Equal(2, export.Count);

        var target = new SavedCommandStore(new InMemoryPluginSettingsProvider().GetSettings(VapixCommanderPlugin.PluginId), TestSecrets.Create());
        var broken = export.Json.Replace("\"custom.dn50\"", "\"Not An Id\"", StringComparison.Ordinal);
        var reply = await target.ImportAsync(broken, "me", CancellationToken.None);

        Assert.Equal(1, reply.Imported);
        Assert.Single(reply.Problems);
        Assert.Contains("Day Night Level 50", reply.Problems[0], StringComparison.Ordinal);

        var again = await target.ImportAsync(export.Json, "me", CancellationToken.None);
        Assert.Equal(2, again.Imported);
        Assert.Equal(2, (await target.ListAsync(CancellationToken.None)).Count);
        Assert.NotEmpty((await target.ImportAsync("{ \"x\": 1 }", null, CancellationToken.None)).Problems);
    }
}
