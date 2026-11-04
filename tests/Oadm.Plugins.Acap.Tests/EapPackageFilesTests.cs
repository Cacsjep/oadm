using System.IO.Compression;
using System.Text;

using Oadm.Plugins.Acap.Client;
using Oadm.Sdk.Testing;
using Oadm.Tests.Shared;

namespace Oadm.Plugins.Acap.Tests;

/// <summary>A ZIP of .eap files: unpacked on the client, its limits, its refusals, the temp folder, both ways through the dialog.</summary>
public sealed class EapPackageFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oadm-eap-zip-" + Guid.NewGuid().ToString("N"));
    private readonly string _temp;

    public EapPackageFilesTests()
    {
        _temp = Path.Combine(_dir, "temp");
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A virus scanner may still hold a file it was just written: the temp folder stays.
        }
    }

    [Fact]
    public void The_sweep_takes_only_old_folders_of_the_dialog()
    {
        var now = DateTimeOffset.UtcNow;
        string Folder(string name, TimeSpan age)
        {
            var folder = Path.Combine(_temp, name);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "a.eap"), "x");
            Directory.SetLastWriteTimeUtc(folder, (now - age).UtcDateTime);
            return folder;
        }

        var old = Folder(EapPackageFiles.FolderPrefix + "old", EapPackageFiles.LeftoverAge + TimeSpan.FromMinutes(1));
        var recent = Folder(EapPackageFiles.FolderPrefix + "recent", EapPackageFiles.LeftoverAge - TimeSpan.FromMinutes(1));
        var other = Folder("other-old", EapPackageFiles.LeftoverAge * 2);

        EapPackageFiles.SweepLeftovers(_temp, now);

        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(other));
    }

    [Fact]
    public void The_sweep_of_a_folder_that_is_not_there_says_nothing() =>
        EapPackageFiles.SweepLeftovers(Path.Combine(_dir, "none"), DateTimeOffset.UtcNow);

    [Fact]
    public async Task The_sweep_runs_once_per_process()
    {
        await EapPackageFiles.SweepOnce();
        Assert.True(EapPackageFiles.SweepOnce().IsCompletedSuccessfully);
        _ = new AcapTaskDialog(); // the client's start asks for it: nothing more happens
    }

    private static byte[] Package(string architecture = "aarch64", string app = "hello", string version = "1.2.0") =>
        EapBuilder.FromManifest(EapBuilder.Manifest(appName: app, friendlyName: app, version: version, architecture: architecture));

    private string Zip(string name, params (string Name, byte[] Content)[] entries) => Write(name, EapBuilder.Zip(entries));

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private EapPackageFiles Files(long maxBytes = EapPackageFiles.MaxUnpackedBytes, int maxEntries = EapPackageFiles.MaxZipEntries) =>
        new() { TempRoot = _temp, MaxBytes = maxBytes, MaxEntries = maxEntries };

    private static (AcapDialogViewModel Vm, FakeDialogContext Ctx) Dialog(params string[] picked)
    {
        var device = new FakeDevice();
        var ctx = new FakeDialogContext();
        ctx.Devices[device.Id] = FakeDialogContext.State();
        return (new AcapDialogViewModel(ctx, [device], new FakeMultiPicker(picked)), ctx);
    }

    /// <summary>Every byte of a ZIP's headers is the file's own word: a field is changed after writing.</summary>
    private static byte[] Patch(byte[] zip, byte first, byte second, int offset, Action<byte[], int> change)
    {
        for (var i = 0; i + offset + 4 < zip.Length; i++)
        {
            if (zip[i] == (byte)'P' && zip[i + 1] == (byte)'K' && zip[i + 2] == first && zip[i + 3] == second)
            {
                change(zip, i + offset);
            }
        }

        return zip;
    }

    [Fact]
    public async Task A_pick_without_a_zip_passes_through_unchanged()
    {
        using var files = Files();

        await files.PickAsync(["C:/a/one.eap", "C:/b/two.eap"], null, CancellationToken.None);

        Assert.Equal(["C:/a/one.eap", "C:/b/two.eap"], files.Paths);
        Assert.Equal("2 files", files.Label);
        Assert.Null(files.Folder);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp));
    }

    [Fact]
    public void The_row_names_the_zip_and_the_files()
    {
        Assert.Equal("a.eap", EapPackageFiles.Describe(["C:/x/a.eap"], null));
        Assert.Equal("2 files", EapPackageFiles.Describe(["a.eap", "b.eap"], null));
        Assert.Equal("a.zip", EapPackageFiles.Describe(["C:/x/a.zip"], null));
        Assert.Equal("a.zip  ·  1 file", EapPackageFiles.Describe(["C:/x/a.zip"], 1));
        Assert.Equal("a.zip  ·  2 files", EapPackageFiles.Describe(["C:/x/a.zip"], 2));
        Assert.Equal("a.ZIP and 2 more  ·  5 files", EapPackageFiles.Describe(["x.eap", "a.ZIP", "b.zip"], 5));
    }

    [Fact]
    public void A_long_zip_name_is_shortened_in_the_middle_so_the_count_stays()
    {
        var name = new string('a', 60) + "_4_12_3_signed.zip";

        var label = EapPackageFiles.Describe([name], 2);

        Assert.Equal(new string('a', 25) + "…2_3_signed.zip  ·  2 files", label);
        Assert.Equal(EapPackageFiles.MaxShownNameLength, label.IndexOf("  ·  ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_zip_of_two_architectures_loads_as_a_set_and_only_its_packages_are_uploaded()
    {
        var zip = Zip("hello.zip", ("hello_aarch64.eap", Package("aarch64")), ("hello_armv7hf.eap", Package("armv7hf")));
        var (vm, ctx) = Dialog(zip);
        using var dialog = vm;
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.Null(vm.PackageError);
        Assert.Equal("hello.zip  ·  2 files", vm.PackageFileName);
        Assert.True(vm.HasSeveralPackages);
        Assert.Equal(["aarch64", "armv7hf"], vm.Packages.Select(p => p.Architecture));
        Assert.Equal("aarch64 build", vm.Compatibility[0].Details);

        string? closed = null;
        vm.CloseRequested += (_, payload) => closed = payload;
        await vm.InstallCommand.ExecuteAsync(null);

        // Never the ZIP: the same two uploads as two loose files.
        Assert.Equal(["hello_aarch64.eap", "hello_armv7hf.eap"], ctx.Uploads.Select(Path.GetFileName));
        Assert.Equal(2, AcapPayload.Parse(closed).AllFiles().Count);
    }

    [Fact]
    public async Task Other_files_and_the_copies_macos_adds_are_ignored()
    {
        var zip = Zip("hello.zip",
            ("hello_aarch64.eap", Package("aarch64")),
            ("readme.txt", Encoding.UTF8.GetBytes("Read me")),
            ("nested.zip", EapBuilder.Zip(("inner.eap", Package("armv7hf")))),
            ("docs/manual.pdf", [1, 2]),
            ("docs/", []),
            ("__MACOSX/._hello_aarch64.eap", [0, 5, 22, 7]),
            ("._hello_armv7hf.eap", [0, 5, 22, 7]));
        var (vm, _) = Dialog(zip);
        using var dialog = vm;
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.Null(vm.PackageError);
        Assert.Equal("hello.zip  ·  1 file", vm.PackageFileName);
        Assert.Equal(["hello_aarch64.eap"], vm.PackageFiles.Paths.Select(Path.GetFileName));
    }

    [Fact]
    public async Task Packages_in_nested_folders_are_found_and_the_upper_case_extension_counts()
    {
        var zip = Zip("hello.ZIP", ("vendor/v1.2/arm64/hello_aarch64.EAP", Package("aarch64")), ("vendor\\v1.2\\arm32\\hello_armv7hf.eap", Package("armv7hf")));
        var (vm, _) = Dialog(zip);
        using var dialog = vm;
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.Null(vm.PackageError);
        Assert.Equal(2, vm.Packages.Count);
        Assert.Equal("hello.ZIP  ·  2 files", vm.PackageFileName);
    }

    [Fact]
    public async Task A_mixed_pick_keeps_its_order()
    {
        var loose = Write("hello_i386.eap", Package("x86_64"));
        var zip = Zip("hello.zip", ("hello_aarch64.eap", Package("aarch64")), ("hello_armv7hf.eap", Package("armv7hf")));
        var (vm, ctx) = Dialog(loose, zip);
        using var dialog = vm;
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.Null(vm.PackageError);
        Assert.Equal("hello.zip and 1 more  ·  3 files", vm.PackageFileName);
        Assert.Equal(["x86_64", "aarch64", "armv7hf"], vm.Packages.Select(p => p.Architecture));
        Assert.Equal(loose, vm.PackageFiles.Paths[0]);
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(3, ctx.Uploads.Count);
    }

    [Fact]
    public async Task A_zip_of_two_different_applications_is_refused_before_any_upload()
    {
        var zip = Zip("bundle.zip", ("a.eap", Package("aarch64")), ("b.eap", Package("armv7hf", app: "other")));
        var (vm, ctx) = Dialog(zip);
        using var dialog = vm;
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.Equal("b.eap is another application (other). Choose the files of one application.", vm.PackageError);
        Assert.False(vm.HasPackage);
        Assert.False(vm.InstallCommand.CanExecute(null));
        Assert.Empty(ctx.Uploads);
    }

    [Fact]
    public async Task A_broken_package_in_a_zip_is_named_in_the_row()
    {
        var zip = Zip("hello.zip", ("a.eap", Package("aarch64")), ("bad.eap", Encoding.UTF8.GetBytes("not a package")));
        var (vm, _) = Dialog(zip);
        using var dialog = vm;
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.StartsWith("bad.eap: ", vm.PackageError, StringComparison.Ordinal);
        Assert.False(vm.HasPackage);
    }

    [Fact]
    public async Task Two_entries_of_the_same_name_do_not_overwrite_each_other()
    {
        var one = Package("aarch64");
        var two = Package("armv7hf");
        var zip = Zip("hello.zip", ("arm64/hello.eap", one), ("arm32/hello.eap", two));
        using var files = Files();

        await files.PickAsync([zip], null, CancellationToken.None);

        Assert.Equal(2, files.Paths.Count);
        Assert.NotEqual(files.Paths[0], files.Paths[1]);
        Assert.Equal(one, await File.ReadAllBytesAsync(files.Paths[0]));
        Assert.Equal(two, await File.ReadAllBytesAsync(files.Paths[1]));
    }

    [Theory]
    [InlineData("../../evil.eap", "evil.eap")]
    [InlineData("..\\..\\evil.eap", "evil.eap")]
    [InlineData("/etc/evil.eap", "evil.eap")]
    [InlineData("C:\\Windows\\evil.eap", "evil.eap")]
    [InlineData("C:evil.eap", "C_evil.eap")]
    [InlineData("hello.eap:stream.eap", "hello.eap_stream.eap")]
    [InlineData("a<b>c|d?e*f\".eap", "a_b_c_d_e_f_.eap")]
    [InlineData("con.eap", "package.eap")]
    [InlineData("NUL.tar.eap", "package.eap")]
    [InlineData("com1.eap", "package.eap")]
    [InlineData(".eap", "package.eap")]
    [InlineData("...eap", "package.eap")]
    [InlineData("hello.EAP", "hello.eap")]
    public async Task An_entry_name_is_never_a_path(string entry, string expected)
    {
        var victim = Path.Combine(_dir, "evil.eap");
        var zip = Zip("hello.zip", (entry, Package()));
        using var files = Files();

        await files.PickAsync([zip], null, CancellationToken.None);

        var path = Assert.Single(files.Paths);
        Assert.Equal(expected, Path.GetFileName(path));
        Assert.StartsWith(files.Folder + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.Ordinal);
        Assert.False(File.Exists(victim));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_temp)!, "evil.eap")));
    }

    [Fact]
    public async Task A_very_long_name_is_cut()
    {
        var zip = Zip("hello.zip", (new string('a', 400) + ".eap", Package()));
        using var files = Files();

        await files.PickAsync([zip], null, CancellationToken.None);

        Assert.Equal(EapPackageFiles.MaxNameLength + ".eap".Length, Path.GetFileName(Assert.Single(files.Paths)).Length);
    }

    [Fact]
    public async Task A_zip_without_packages_says_so()
    {
        var zip = Zip("manual.zip", ("readme.txt", [1]), ("__MACOSX/._a.eap", [1]));
        var (vm, _) = Dialog(zip);
        using var dialog = vm;
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.Equal("manual.zip has no .eap files.", vm.PackageError);
        Assert.Equal("manual.zip", vm.PackageFileName);
        Assert.False(vm.HasPackage);
    }

    [Fact]
    public async Task A_broken_zip_says_so()
    {
        var text = Write("text.zip", Encoding.UTF8.GetBytes("not a zip"));
        var empty = Write("empty.zip", []);
        var good = EapBuilder.Zip(("a.eap", Package()));
        var cut = Write("cut.zip", good[..(good.Length / 2)]);
        var (vm, ctx) = Dialog(text);
        using var dialog = vm;
        await vm.InitializeAsync();

        foreach (var (path, name) in new[] { (text, "text.zip"), (empty, "empty.zip"), (cut, "cut.zip") })
        {
            await vm.LoadPackageAsync(path);
            Assert.Equal($"{name} is damaged or not a ZIP file.", vm.PackageError);
            Assert.False(vm.HasPackage);
        }

        Assert.Empty(ctx.Uploads);
    }

    [Fact]
    public async Task A_password_protected_zip_says_so()
    {
        var zip = EapBuilder.Zip(("a.eap", Package()));
        Patch(zip, 3, 4, 6, static (bytes, at) => bytes[at] |= 1);
        Patch(zip, 1, 2, 8, static (bytes, at) => bytes[at] |= 1);
        var path = Write("locked.zip", zip);
        using var files = Files();

        var refused = await Assert.ThrowsAsync<InvalidEapException>(() => files.PickAsync([path], null, CancellationToken.None));

        Assert.Equal("locked.zip is password protected.", refused.Message);
    }

    [Fact]
    public async Task More_files_than_one_install_takes_are_refused()
    {
        var entries = Enumerable.Range(0, AcapPayload.MaxFiles + 1).Select(i => ($"p{i}.eap", Package())).ToArray();
        var (vm, ctx) = Dialog(Zip("many.zip", entries));
        using var dialog = vm;
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.Equal("Choose at most 16 files.", vm.PackageError);
        Assert.Empty(ctx.Uploads);
    }

    [Fact]
    public async Task The_file_limit_counts_a_mixed_pick_and_writes_nothing()
    {
        var entries = Enumerable.Range(0, AcapPayload.MaxFiles).Select(i => ($"p{i}.eap", Package())).ToArray();
        var zip = Zip("full.zip", entries);
        var loose = Write("one_more.eap", Package());
        using var files = Files();

        var refused = await Assert.ThrowsAsync<InvalidEapException>(() => files.PickAsync([zip, loose], null, CancellationToken.None));

        Assert.Equal(AcapCompatibility.TooManyFiles, refused.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp));
    }

    [Fact]
    public async Task Exactly_the_file_limit_is_allowed()
    {
        var entries = Enumerable.Range(0, AcapPayload.MaxFiles).Select(i => ($"p{i}.eap", Package())).ToArray();
        var zip = Zip("full.zip", entries);
        using var files = Files();

        await files.PickAsync([zip], null, CancellationToken.None);

        Assert.Equal(AcapPayload.MaxFiles, files.Paths.Count);
    }

    [Fact]
    public async Task The_size_a_zip_declares_is_refused_before_anything_is_written()
    {
        var zip = Zip("big.zip", ("a.eap", new byte[600]), ("b.eap", new byte[600]));
        using var files = Files(maxBytes: 1_000);

        var refused = await Assert.ThrowsAsync<InvalidEapException>(() => files.PickAsync([zip], null, CancellationToken.None));

        Assert.Equal("big.zip is too large to unpack.", refused.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp));
    }

    [Fact]
    public async Task The_size_limit_counts_every_zip_of_a_pick()
    {
        var one = Zip("one.zip", ("a.eap", new byte[600]));
        var two = Zip("two.zip", ("b.eap", new byte[600]));
        using var files = Files(maxBytes: 1_000);

        var refused = await Assert.ThrowsAsync<InvalidEapException>(() => files.PickAsync([one, two], null, CancellationToken.None));

        Assert.Equal("two.zip is too large to unpack.", refused.Message);
    }

    [Fact]
    public async Task A_zip_that_lies_about_its_size_is_stopped_while_it_is_written()
    {
        // The entry says 10 bytes and holds 200,000: the writing stops, whatever the header claims.
        var zip = EapBuilder.Zip(("a.eap", new byte[200_000]));
        Patch(zip, 3, 4, 22, static (bytes, at) => BitConverter.TryWriteBytes(bytes.AsSpan(at, 4), 10));
        Patch(zip, 1, 2, 24, static (bytes, at) => BitConverter.TryWriteBytes(bytes.AsSpan(at, 4), 10));
        var path = Write("bomb.zip", zip);
        using var files = Files(maxBytes: 100_000);

        var refused = await Assert.ThrowsAsync<InvalidEapException>(() => files.PickAsync([path], null, CancellationToken.None));

        Assert.StartsWith("bomb.zip ", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp));
    }

    [Fact]
    public async Task A_zip_with_too_many_entries_is_refused()
    {
        var zip = Zip("junk.zip", ("a.eap", Package()), ("1.txt", [1]), ("2.txt", [1]), ("3.txt", [1]));
        using var files = Files(maxEntries: 3);

        var refused = await Assert.ThrowsAsync<InvalidEapException>(() => files.PickAsync([zip], null, CancellationToken.None));

        Assert.Equal("junk.zip has too many files.", refused.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp));
    }

    [Fact]
    public void The_limits_match_the_largest_package_and_the_payload()
    {
        Assert.Equal(2048L * 1024 * 1024, EapPackageFiles.MaxUnpackedBytes); // ServerSettings.DefaultUploadsMaxMegabytes
        Assert.Equal(16, AcapPayload.MaxFiles);
    }

    [Fact]
    public async Task Progress_reaches_the_end()
    {
        var seen = new List<double>();
        var zip = Zip("hello.zip", ("a.eap", new byte[200_000]));
        using var files = Files();

        await files.PickAsync([zip], new Inline(seen.Add), CancellationToken.None);

        Assert.Equal(1.0, seen[^1]);
        Assert.Equal(seen.OrderBy(v => v), seen);
    }

    [Fact]
    public async Task A_new_pick_deletes_the_last_one()
    {
        var zip = Zip("hello.zip", ("a.eap", Package()));
        using var files = Files();
        await files.PickAsync([zip], null, CancellationToken.None);
        var first = files.Folder;

        await files.PickAsync([zip], null, CancellationToken.None);

        Assert.False(Directory.Exists(first));
        Assert.Single(Directory.EnumerateDirectories(_temp));

        await files.PickAsync(["C:/a/one.eap"], null, CancellationToken.None);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp));
    }

    [Fact]
    public async Task A_cancelled_unpack_leaves_nothing()
    {
        // The progress callback holds the unpacking until the test has cancelled it.
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cts = new CancellationTokenSource();
        var zip = Zip("hello.zip", ("a.eap", new byte[400_000]));
        using var files = Files();

        var pick = files.PickAsync([zip], new Inline(_ =>
        {
            reached.Set();
            release.Wait();
        }), cts.Token);
        await Eventually.TrueAsync(() => reached.IsSet, "the unpacking started");
        await cts.CancelAsync();
        release.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pick);
        Assert.Null(files.Folder);
        Assert.Empty(files.Paths);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp));
    }

    [Fact]
    public async Task Closing_during_an_unpack_stops_it()
    {
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var zip = Zip("hello.zip", ("a.eap", new byte[400_000]));
        var files = Files();

        var pick = files.PickAsync([zip], new Inline(_ =>
        {
            reached.Set();
            release.Wait();
        }), CancellationToken.None);
        await Eventually.TrueAsync(() => reached.IsSet, "the unpacking started");
        files.Dispose();
        release.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pick);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp));
    }

    [Fact]
    public async Task Close_waits_for_a_hold()
    {
        var zip = Zip("hello.zip", ("a.eap", Package()));
        var files = Files();
        await files.PickAsync([zip], null, CancellationToken.None);
        var path = files.Paths[0];

        var hold = files.Hold();
        files.Dispose();
        Assert.True(File.Exists(path));

        hold.Dispose();
        hold.Dispose();
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_temp));
    }

    [Fact]
    public async Task Cancel_and_close_delete_the_unpacked_files()
    {
        var zip = Zip("hello.zip", ("a.eap", Package()));
        var (vm, _) = Dialog(zip);
        await vm.InitializeAsync();
        await vm.PickPackageCommand.ExecuteAsync(null);
        var folder = vm.PackageFiles.Folder;
        Assert.True(Directory.Exists(folder));
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.CancelCommand.Execute(null);

        Assert.True(closed);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task A_new_pick_in_the_dialog_deletes_the_last_one()
    {
        var zip = Zip("hello.zip", ("a.eap", Package()));
        var (vm, _) = Dialog(zip);
        using var dialog = vm;
        await vm.InitializeAsync();
        await vm.LoadPackageAsync(zip);
        var first = vm.PackageFiles.Folder;

        await vm.LoadPackageAsync(Write("hello.eap", Package()));

        Assert.False(Directory.Exists(first));
        Assert.Null(vm.PackageFiles.Folder);
        Assert.Equal("hello.eap", vm.PackageFileName);
    }

    [Fact]
    public async Task A_failed_read_deletes_the_unpacked_files()
    {
        var zip = Zip("bad.zip", ("bad.eap", Encoding.UTF8.GetBytes("not a package")));
        var (vm, _) = Dialog(zip);
        using var dialog = vm;
        await vm.InitializeAsync();

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.NotNull(vm.PackageError);
        Assert.Null(vm.PackageFiles.Folder);
        Assert.Empty(vm.PackageFiles.Paths);
    }

    [Fact]
    public async Task The_files_stay_until_the_upload_ended_also_when_the_window_closes()
    {
        var zip = Zip("hello.zip", ("hello_aarch64.eap", Package("aarch64")), ("hello_armv7hf.eap", Package("armv7hf")));
        var (vm, ctx) = Dialog(zip);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.UploadGate = gate.Task;
        await vm.InitializeAsync();
        await vm.PickPackageCommand.ExecuteAsync(null);
        var folder = vm.PackageFiles.Folder;
        var paths = vm.PackageFiles.Paths;

        var install = vm.InstallCommand.ExecuteAsync(null);
        await Eventually.TrueAsync(() => ctx.Uploads.Count == 1, "the upload started");
        vm.Dispose(); // the window's X while the upload runs
        Assert.All(paths, p => Assert.True(File.Exists(p)));

        gate.SetResult();
        await install;

        Assert.Equal(2, ctx.Uploads.Count);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task A_failed_upload_keeps_the_files_for_the_next_try()
    {
        var zip = Zip("hello.zip", ("hello_aarch64.eap", Package("aarch64")));
        var (vm, ctx) = Dialog(zip);
        using var dialog = vm;
        ctx.UploadFailure = new InvalidOperationException("no room");
        await vm.InitializeAsync();
        await vm.PickPackageCommand.ExecuteAsync(null);

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.NotNull(vm.PackageError);
        Assert.True(File.Exists(vm.PackageFiles.Paths[0]));

        ctx.UploadFailure = null;
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.NotNull(vm.Result);
    }

    [Fact]
    public async Task Cancel_stops_an_unpack_in_the_dialog_and_keeps_it_open()
    {
        // The click arrives the moment the progress row shows: the command is on the row's side, never racing the unpack.
        var zip = Zip("hello.zip", ("a.eap", Package()));
        var (vm, ctx) = Dialog(zip);
        using var dialog = vm;
        await vm.InitializeAsync();
        var closed = false;
        var row = false;
        vm.CloseRequested += (_, _) => closed = true;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.IsBusy) && vm.IsBusy)
            {
                row = vm.UploadStatus == EapPackageFiles.Unpacking && !vm.PickPackageCommand.CanExecute(null);
                vm.CancelCommand.Execute(null);
            }
        };

        await vm.LoadPackageAsync(zip);

        Assert.True(row);
        Assert.False(closed);
        Assert.False(vm.IsBusy);
        Assert.Equal(EapPackageFiles.UnpackCancelled, vm.UploadStatus);
        Assert.False(vm.HasPackage);
        Assert.Null(vm.PackageError);
        Assert.Null(vm.PackageFiles.Folder);
        Assert.Empty(ctx.Uploads);
    }

    [Fact]
    public async Task Loose_files_show_no_unpacking()
    {
        var eap = Write("hello.eap", Package());
        var (vm, _) = Dialog(eap);
        using var dialog = vm;
        await vm.InitializeAsync();
        var busy = false;
        vm.PropertyChanged += (_, e) => busy |= e.PropertyName == nameof(vm.IsBusy) && vm.IsBusy;

        await vm.PickPackageCommand.ExecuteAsync(null);

        Assert.False(busy);
        Assert.Equal("hello.eap", vm.PackageFileName);
        Assert.Equal(eap, vm.PackageFiles.Paths[0]);
    }

    [Fact]
    public void Both_dialogs_use_the_shared_piece_and_neither_opens_a_zip_itself()
    {
        Assert.Equal(typeof(EapPackageFiles), typeof(AcapDialogViewModel).GetProperty(nameof(AcapDialogViewModel.PackageFiles))!.PropertyType);
        foreach (var source in new[]
        {
            TestRepository.Path("plugins", "Oadm.Plugins.Acap.Client", "AcapDialogViewModel.cs"),
            TestRepository.Path("plugins", "Oadm.Plugins.AppManagement.Client", "InstallAppsViewModel.cs"),
        })
        {
            var code = File.ReadAllText(source);
            Assert.Contains("EapPackageFiles", code, StringComparison.Ordinal);
            Assert.DoesNotContain("System.IO.Compression", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ZipFile", code, StringComparison.Ordinal);
            Assert.DoesNotContain("ZipArchive", code, StringComparison.Ordinal);
        }
    }

    /// <summary>Reports on the calling thread, unlike <see cref="Progress{T}"/>.</summary>
    private sealed class Inline(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
