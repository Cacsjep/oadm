using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Client.Infrastructure;

namespace Oadm.Client.Tests;

/// <summary>
/// The client settings file holds session tokens and the server pins: it is replaced as a whole (never half written) and,
/// on Linux and macOS, readable by its owner only.
/// </summary>
public sealed class ClientSettingsFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "oadm-client-settings-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_folder, "client-settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private JsonClientSettingsStore Store() => new(new AppOptions { DataFolder = _folder }, NullLogger<JsonClientSettingsStore>.Instance);

    [Fact]
    public void Save_replaces_the_file_and_leaves_nothing_else_in_the_folder()
    {
        JsonClientSettingsStore store = Store();
        store.Current.RememberedLogins["cam:5080"] = new RememberedLogin { UserName = "anna", Token = "token-1" };
        store.Save();
        store.Current.RememberedLogins["cam:5080"].Token = "token-2";
        store.Current.ServerAddress = "http://localhost:5080"; // kept as typed
        store.Save();

        Assert.Equal(["client-settings.json"], Directory.GetFiles(_folder).Select(Path.GetFileName));
        ClientSettings again = Store().Current;
        Assert.Equal("token-2", again.RememberedLogins["cam:5080"].Token);
        Assert.Equal("anna", again.RememberedLogins["cam:5080"].UserName);
        Assert.Equal("http://localhost:5080", again.ServerAddress);
    }

    [Fact]
    public void A_save_that_fails_halfway_keeps_the_old_file()
    {
        JsonClientSettingsStore store = Store();
        store.Current.RememberedLogins["cam:5080"] = new RememberedLogin { UserName = "anna", Token = "token-1" };
        store.Save();
        string before = File.ReadAllText(SettingsPath);

        // The disk is full after half of the new file.
        Assert.Throws<IOException>(() => JsonClientSettingsStore.Replace(SettingsPath, stream =>
        {
            stream.Write("{ \"RememberedLogins\": { \"cam:5080\": { \"Us"u8);
            throw new IOException("There is not enough space on the disk.");
        }));

        Assert.Equal(before, File.ReadAllText(SettingsPath));
        Assert.Equal(["client-settings.json"], Directory.GetFiles(_folder).Select(Path.GetFileName));
        Assert.Equal("token-1", Store().Current.RememberedLogins["cam:5080"].Token);
    }

    [Fact]
    public void What_an_interrupted_save_left_is_removed_at_the_next_start()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsPath, """{ "ServerAddress": "cam:5080" }""");
        string leftover = SettingsPath + ".0123456789abcdef.tmp"; // the client was ended while it saved: tokens inside
        File.WriteAllText(leftover, """{ "RememberedLogins": { "cam:5080": { "Token": "token-1" } } }""");
        string other = Path.Combine(_folder, "notes.tmp");
        File.WriteAllText(other, "not ours");

        JsonClientSettingsStore store = Store();

        Assert.Equal("cam:5080", store.Current.ServerAddress);
        Assert.False(File.Exists(leftover));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void On_Linux_and_macOS_the_file_is_readable_by_its_owner_only()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows: the rights of the user's data folder
        }

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        // A file of an older version, readable by everyone: limited once when the client starts.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsPath, """{ "ServerAddress": "cam:5080" }""");
        File.SetUnixFileMode(SettingsPath, ownerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        JsonClientSettingsStore store = Store();
        Assert.Equal("cam:5080", store.Current.ServerAddress);
        Assert.Equal(ownerOnly, File.GetUnixFileMode(SettingsPath));

        // Every save keeps it so, also a first one.
        store.Save();
        Assert.Equal(ownerOnly, File.GetUnixFileMode(SettingsPath));
        File.Delete(SettingsPath);
        store.Save();
        Assert.Equal(ownerOnly, File.GetUnixFileMode(SettingsPath));
    }
}
