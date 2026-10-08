using System.Text;

using Grpc.Core;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Oadm.Core.Security;
using Oadm.Server.Discovery;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests;

/// <summary>The fast add page on the server: automatic login, credential list, RetryAuth, address probe, Commit.</summary>
public sealed class FastAddTests
{
    private const string Password = "s3cret-Pass";

    private static FakeAxisNetwork Network()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.0.1", FakeSerials.Make(1), Password);
        network.Add("10.9.0.2", FakeSerials.Make(2), "other-password");
        network.Add("10.9.0.3", FakeSerials.Make(3), password: null); // factory default
        return network;
    }

    [Fact]
    public async Task ScanLogsInWithTheCredentialListAndCommitUsesTheMatchedCredential()
    {
        var network = Network();
        await using var host = await TestServerHost.StartAsync(network);
        var entry = await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = Password });

        var (session, devices) = await TestHelpers.ScanWithLoginAsync(host, "10.9.0.1", "10.9.0.3");

        var ok = devices["10.9.0.1"];
        Assert.Equal(Proto.AuthState.Authenticated, ok.AuthState);
        Assert.Equal("root", ok.AuthUserName);
        Assert.Equal("list:" + Guid.Parse(entry.Id).ToString("N"), ok.CredentialId);
        Assert.Equal(Proto.AuthState.LoginFailed, devices["10.9.0.2"].AuthState);
        Assert.Equal("The known credential did not work.", devices["10.9.0.2"].AuthDetail);
        Assert.Equal(Proto.AuthState.PasswordNotSet, devices["10.9.0.3"].AuthState);
        Assert.Equal("none", devices["10.9.0.3"].PassphrasePolicy);
        Assert.All(devices.Values, d => Assert.Equal(Proto.DiscoverySource.RangeScan, d.Source));

        // Commit without any credentials in the request: the server uses the one that worked.
        var reply = await host.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = session, DiscoveredIds = { ok.DiscoveredId } });
        var result = Assert.Single(reply.Results);
        Assert.Equal(Proto.DeviceStatus.Ok, result.Status);
        Assert.Equal(Assert.Single(reply.DeviceIds), result.DeviceId);
        var stored = await host.Get<CredentialStore>().GetAsync(Guid.Parse(result.DeviceId), CancellationToken.None);
        Assert.Equal(("root", Password), (stored!.UserName, stored.Password));

        // The finished session now reports the device as added.
        var again = await TestHelpers.WatchToEndAsync(host, session);
        Assert.Equal(Proto.AuthState.AlreadyAdded, again[ok.DiscoveredId].AuthState);
        Assert.True(again[ok.DiscoveredId].AlreadyManaged);
    }

    [Fact]
    public async Task PasswordsOfManagedDevicesAreNeverTriedOnNewDevices()
    {
        var network = Network();
        network.Add("10.9.0.5", FakeSerials.Make(5), Password);
        await using var host = await TestServerHost.StartAsync(network);

        // No credential list: nothing to try.
        var (first, scanned) = await TestHelpers.ScanWithLoginAsync(host, "10.9.0.1", "10.9.0.1");
        Assert.Equal(Proto.AuthState.LoginFailed, scanned["10.9.0.1"].AuthState);
        Assert.StartsWith("No known credentials", scanned["10.9.0.1"].AuthDetail, StringComparison.Ordinal);
        Assert.Equal(0, network["10.9.0.1"].RejectedLogins);

        // Legacy explicit credentials add the first device; its password is stored for it ...
        var added = await host.AddDevices.CommitAsync(new Proto.CommitRequest
        {
            SessionId = first,
            DiscoveredIds = { FakeSerials.Make(1) },
            Credentials = { new Proto.DeviceCredentials { UserName = "root", Password = Password } },
        });
        Assert.Single(added.DeviceIds);

        // ... but never sent to a new device (production hardening: credential list only).
        var (_, next) = await TestHelpers.ScanWithLoginAsync(host, "10.9.0.5", "10.9.0.5");
        Assert.Equal(Proto.AuthState.LoginFailed, next["10.9.0.5"].AuthState);
        Assert.StartsWith("No known credentials", next["10.9.0.5"].AuthDetail, StringComparison.Ordinal);
        Assert.Empty(network["10.9.0.5"].CredentialRequests);
    }

    [Fact]
    public async Task AtMostTenKnownCredentialsAreTriedPerDevice()
    {
        var network = Network();
        await using var host = await TestServerHost.StartAsync(network);
        for (var i = 0; i < 12; i++)
        {
            await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "user" + i, Password = "wrong-" + i });
        }

        var (_, devices) = await TestHelpers.ScanWithLoginAsync(host, "10.9.0.2", "10.9.0.2");

        Assert.Equal(Proto.AuthState.LoginFailed, devices["10.9.0.2"].AuthState);
        Assert.Equal("None of the 10 known credentials worked.", devices["10.9.0.2"].AuthDetail);
        Assert.Equal(10, network["10.9.0.2"].RejectedLogins);
    }

    [Fact]
    public async Task RetryAuthLogsInSavesToTheListAndReportsFailures()
    {
        var network = Network();
        await using var host = await TestServerHost.StartAsync(network);
        var (session, devices) = await TestHelpers.ScanWithLoginAsync(host, "10.9.0.1", "10.9.0.3");
        var failed = devices["10.9.0.2"];
        Assert.Equal(Proto.AuthState.LoginFailed, failed.AuthState);

        var wrong = await host.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest { SessionId = session, DiscoveredId = failed.DiscoveredId, UserName = "root", Password = "nope" });
        Assert.Equal(Proto.AuthState.LoginFailed, wrong.AuthState);
        Assert.Equal("The user name or password is wrong.", wrong.AuthDetail);

        var right = await host.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest
        {
            SessionId = session,
            DiscoveredId = failed.DiscoveredId,
            UserName = " root ",
            Password = "other-password",
            SaveToCredentialList = true,
        });
        Assert.Equal(Proto.AuthState.Authenticated, right.AuthState);
        Assert.Equal("root", right.AuthUserName);
        Assert.StartsWith("list:", right.CredentialId, StringComparison.Ordinal);
        var list = await host.Settings.ListCredentialsAsync(new Proto.Empty());
        Assert.Equal("root", Assert.Single(list.Entries).UserName);

        // Not saved: kept in server memory for the add only.
        var plain = await host.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest { SessionId = session, DiscoveredId = devices["10.9.0.1"].DiscoveredId, UserName = "root", Password = Password });
        Assert.Equal(Proto.AuthState.Authenticated, plain.AuthState);
        Assert.Equal("entered", plain.CredentialId);
        Assert.Single((await host.Settings.ListCredentialsAsync(new Proto.Empty())).Entries);

        var reply = await host.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = session, DiscoveredIds = { failed.DiscoveredId, devices["10.9.0.1"].DiscoveredId } });
        Assert.All(reply.Results, r => Assert.Equal(Proto.DeviceStatus.Ok, r.Status));

        // Errors: factory default, unknown device, empty password, already added.
        var factory = await Assert.ThrowsAsync<RpcException>(() => host.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest { SessionId = session, DiscoveredId = devices["10.9.0.3"].DiscoveredId, UserName = "root", Password = "x" }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, factory.StatusCode);
        var unknown = await Assert.ThrowsAsync<RpcException>(() => host.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest { SessionId = session, DiscoveredId = "FFFFFFFFFFFF", UserName = "root", Password = "x" }).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);
        var empty = await Assert.ThrowsAsync<RpcException>(() => host.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest { SessionId = session, DiscoveredId = failed.DiscoveredId, UserName = "root" }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, empty.StatusCode);
        var managed = await Assert.ThrowsAsync<RpcException>(() => host.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest { SessionId = session, DiscoveredId = failed.DiscoveredId, UserName = "root", Password = "x" }).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, managed.StatusCode);
    }

    [Fact]
    public async Task AWorkingRetryIsTriedOnTheFailedDevicesOfThePage()
    {
        var network = new FakeAxisNetwork();
        network.Add("10.9.1.1", FakeSerials.Make(11), "site-Pass");
        network.Add("10.9.1.2", FakeSerials.Make(12), "site-Pass");
        network.Add("10.9.1.3", FakeSerials.Make(13), "another-Pass");
        network.Add("10.9.2.1", FakeSerials.Make(21), "site-Pass");   // other search of the same page
        network.Add("10.9.3.1", FakeSerials.Make(31), "site-Pass");   // search of another page
        await using var host = await TestServerHost.StartAsync(network);
        var (first, page) = await TestHelpers.ScanWithLoginAsync(host, "10.9.1.1", "10.9.1.3");
        var (second, other) = await TestHelpers.ScanWithLoginAsync(host, "10.9.2.1", "10.9.2.1");
        var (unrelated, _) = await TestHelpers.ScanWithLoginAsync(host, "10.9.3.1", "10.9.3.1");
        Assert.All(page.Values.Concat(other.Values), d => Assert.Equal(Proto.AuthState.LoginFailed, d.AuthState));

        var retry = await host.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest
        {
            SessionId = first,
            DiscoveredId = page["10.9.1.1"].DiscoveredId,
            UserName = "root",
            Password = "site-Pass",
            RelatedSessionIds = { second },
        });
        Assert.Equal(Proto.AuthState.Authenticated, retry.AuthState);

        // The finished sessions are watched again: the follow-up logins report back, then the stream ends.
        var firstNow = (await TestHelpers.WatchToEndAsync(host, first)).Values.ToDictionary(d => d.Address);
        var secondNow = (await TestHelpers.WatchToEndAsync(host, second)).Values.ToDictionary(d => d.Address);
        var unrelatedNow = (await TestHelpers.WatchToEndAsync(host, unrelated)).Values.Single();

        Assert.Equal(Proto.AuthState.Authenticated, firstNow["10.9.1.2"].AuthState);
        Assert.Equal(DiscoveryAuthenticatorEntered, firstNow["10.9.1.2"].CredentialId);
        Assert.Equal("root", firstNow["10.9.1.2"].AuthUserName);
        Assert.Equal(Proto.AuthState.LoginFailed, firstNow["10.9.1.3"].AuthState);
        Assert.Equal("The known credential did not work.", firstNow["10.9.1.3"].AuthDetail);
        Assert.Equal(1, network["10.9.1.3"].RejectedLogins);
        Assert.Equal(Proto.AuthState.Authenticated, secondNow["10.9.2.1"].AuthState);
        Assert.Equal(Proto.AuthState.LoginFailed, unrelatedNow.AuthState); // not saved, other page: not tried
        Assert.Equal(0, network["10.9.3.1"].RejectedLogins);

        // Commit stores the credential that worked in the follow-up.
        var reply = await host.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = second, DiscoveredIds = { secondNow["10.9.2.1"].DiscoveredId } });
        var stored = await host.Get<CredentialStore>().GetAsync(Guid.Parse(Assert.Single(reply.DeviceIds)), CancellationToken.None);
        Assert.Equal(("root", "site-Pass"), (stored!.UserName, stored.Password));
    }

    [Fact]
    public async Task ACredentialAddedToTheListIsTriedOnFailedDevices()
    {
        var network = Network();
        await using var host = await TestServerHost.StartAsync(network);
        var (session, devices) = await TestHelpers.ScanWithLoginAsync(host, "10.9.0.1", "10.9.0.2");
        Assert.All(devices.Values, d => Assert.Equal(Proto.AuthState.LoginFailed, d.AuthState));

        var entry = await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = Password });
        await TestHelpers.WaitUntilAsync(
            async () => (await TestHelpers.WatchToEndAsync(host, session)).Values.Single(d => d.Address == "10.9.0.1").AuthState == Proto.AuthState.Authenticated,
            "follow-up login with the new list entry");

        var now = (await TestHelpers.WatchToEndAsync(host, session)).Values.ToDictionary(d => d.Address);
        Assert.Equal("list:" + Guid.Parse(entry.Id).ToString("N"), now["10.9.0.1"].CredentialId);
        Assert.Equal(Proto.AuthState.LoginFailed, now["10.9.0.2"].AuthState);
        Assert.Equal(1, network["10.9.0.2"].RejectedLogins);

        // Removing an entry tries nothing new.
        await host.Settings.RemoveCredentialAsync(new Proto.CredentialEntryId { Id = entry.Id });
        await Task.Delay(200);
        Assert.Equal(1, network["10.9.0.2"].RejectedLogins);
    }

    [Fact]
    public async Task FollowUpLoginsRespectTheAttemptLimitPerDevice()
    {
        var network = Network();
        await using var host = await TestServerHost.StartAsync(network);
        for (var i = 0; i < 10; i++)
        {
            await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "user" + i, Password = "wrong-" + i });
        }

        var (session, devices) = await TestHelpers.ScanWithLoginAsync(host, "10.9.0.1", "10.9.0.2");
        Assert.Equal(10, network["10.9.0.2"].RejectedLogins);

        // The right password of 10.9.0.1 also goes into the list: 10.9.0.2 has no attempts left.
        var retry = await host.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest
        {
            SessionId = session,
            DiscoveredId = devices["10.9.0.1"].DiscoveredId,
            UserName = "root",
            Password = Password,
            SaveToCredentialList = true,
        });
        Assert.Equal(Proto.AuthState.Authenticated, retry.AuthState);

        var now = (await TestHelpers.WatchToEndAsync(host, session)).Values.ToDictionary(d => d.Address);
        Assert.Equal(Proto.AuthState.LoginFailed, now["10.9.0.2"].AuthState);
        Assert.Equal("None of the 10 known credentials worked.", now["10.9.0.2"].AuthDetail);
        Assert.Equal(10, network["10.9.0.2"].RejectedLogins);
    }

    [Fact]
    public async Task StopScanEndsAZeroConfScanAndKeepsTheSession()
    {
        await using var host = await TestServerHost.StartAsync();
        var session = await host.Discovery.StartZeroConfAsync(new Proto.Empty());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var call = host.Discovery.WatchDiscovered(session, cancellationToken: cts.Token);

        await host.Discovery.StopScanAsync(session);

        var messages = new List<Proto.DiscoveredDevice>();
        await foreach (var message in call.ResponseStream.ReadAllAsync(cts.Token))
        {
            messages.Add(message);
        }

        Assert.True(Assert.Single(messages).ScanFinished);
        var again = await TestHelpers.WatchToEndAsync(host, session.SessionId); // still known until Stop
        Assert.Empty(again);
        await host.Discovery.StopScanAsync(new Proto.DiscoverySession { SessionId = "unknown" }); // ignored
        await host.Discovery.StopAsync(session);
    }

    private const string DiscoveryAuthenticatorEntered = "entered";

    [Fact]
    public async Task RetryAuthOnADeviceThatWentAwayIsUnreachable()
    {
        var network = Network();
        await using var host = await TestServerHost.StartAsync(network);
        var (session, devices) = await TestHelpers.ScanWithLoginAsync(host, "10.9.0.2", "10.9.0.2");
        network["10.9.0.2"].Offline = true;

        var result = await host.AddDevices.RetryAuthAsync(new Proto.RetryAuthRequest { SessionId = session, DiscoveredId = devices["10.9.0.2"].DiscoveredId, UserName = "root", Password = "other-password" });

        Assert.Equal(Proto.AuthState.Unreachable, result.AuthState);
        Assert.StartsWith("The device did not answer", result.AuthDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FactoryDefaultDevicesGetTheirPerDevicePassword()
    {
        var network = Network();
        network.Add("10.9.0.4", FakeSerials.Make(4), password: null);
        await using var host = await TestServerHost.StartAsync(network);
        var (session, devices) = await TestHelpers.ScanWithLoginAsync(host, "10.9.0.3", "10.9.0.4");
        Assert.All(devices.Values, d => Assert.Equal(Proto.AuthState.PasswordNotSet, d.AuthState));

        var bad = await Assert.ThrowsAsync<RpcException>(() => host.AddDevices.CommitAsync(new Proto.CommitRequest
        {
            SessionId = session,
            DiscoveredIds = { devices["10.9.0.3"].DiscoveredId },
            InitialPasswords = { [devices["10.9.0.3"].DiscoveredId] = "badépassword" },
        }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, bad.StatusCode);
        Assert.Equal(0, network["10.9.0.3"].PwdgrpCalls);

        var reply = await host.AddDevices.CommitAsync(new Proto.CommitRequest
        {
            SessionId = session,
            DiscoveredIds = { devices["10.9.0.3"].DiscoveredId, devices["10.9.0.4"].DiscoveredId },
            InitialPasswords = { [devices["10.9.0.3"].DiscoveredId] = "first-Pass3" },
            InitialRootPassword = "all-Pass",
        });

        Assert.Equal(2, reply.DeviceIds.Count);
        Assert.All(reply.Results, r => Assert.Equal(Proto.DeviceStatus.Ok, r.Status));
        Assert.Equal("first-Pass3", network["10.9.0.3"].Password);
        Assert.Equal("all-Pass", network["10.9.0.4"].Password);
        Assert.Equal(0, network["10.9.0.3"].PasswordInUrl);
    }

    [Fact]
    public async Task ProbeAddressFindsOneDeviceAndAddsItUnderTheEnteredAddress()
    {
        var network = Network();
        await using var host = await TestServerHost.StartAsync(network);
        await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = Password });

        var session = await host.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "http://10.9.0.1:80" });
        var found = Assert.Single((await TestHelpers.WatchToEndAsync(host, session.SessionId)).Values);
        Assert.Equal(Proto.DiscoverySource.Manual, found.Source);
        Assert.Equal("10.9.0.1:80", found.EnteredAddress);
        Assert.Equal("http", found.Scheme);
        Assert.Equal(Proto.AuthState.Authenticated, found.AuthState);

        var reply = await host.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = session.SessionId, DiscoveredIds = { found.DiscoveredId } });
        var device = await TestHelpers.GetDeviceAsync(host, Assert.Single(reply.DeviceIds));
        Assert.Equal("10.9.0.1:80", device.Address);
        Assert.Equal(Proto.DeviceStatus.Ok, device.Status);

        // Nothing at the address: the session just finishes empty.
        var empty = await host.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "10.9.0.200" });
        Assert.Empty(await TestHelpers.WatchToEndAsync(host, empty.SessionId));

        var invalid = await Assert.ThrowsAsync<RpcException>(() => host.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "ftp://10.9.0.1" }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, invalid.StatusCode);
    }

    [Fact]
    public async Task ProbeAddressWithCredentialsTriesThemFirstForThatDeviceOnly()
    {
        var network = Network();
        network["10.9.0.2"].User = "admin";
        await using var host = await TestServerHost.StartAsync(network);
        await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = Password });

        // A line of an imported device list: its credentials log in to that device, before the credential list.
        var session = await host.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "10.9.0.2", UserName = " admin ", Password = "other-password" });
        var found = Assert.Single((await TestHelpers.WatchToEndAsync(host, session.SessionId)).Values);
        Assert.Equal(Proto.AuthState.Authenticated, found.AuthState);
        Assert.Equal("admin", found.AuthUserName);
        Assert.Equal("entered", found.CredentialId);

        // Never saved to the credential list; Commit stores them for the device.
        Assert.Single((await host.Settings.ListCredentialsAsync(new Proto.Empty())).Entries);
        var reply = await host.AddDevices.CommitAsync(new Proto.CommitRequest { SessionId = session.SessionId, DiscoveredIds = { found.DiscoveredId } });
        var stored = await host.Get<CredentialStore>().GetAsync(Guid.Parse(Assert.Single(reply.DeviceIds)), CancellationToken.None);
        Assert.Equal(("admin", "other-password"), (stored!.UserName, stored.Password));

        // Wrong credentials in the file: the credential list still logs in.
        var wrong = await host.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "10.9.0.1", UserName = "root", Password = "nope" });
        var listed = Assert.Single((await TestHelpers.WatchToEndAsync(host, wrong.SessionId)).Values);
        Assert.Equal(Proto.AuthState.Authenticated, listed.AuthState);
        Assert.StartsWith("list:", listed.CredentialId, StringComparison.Ordinal);

        // Only both or none.
        var half = await Assert.ThrowsAsync<RpcException>(() => host.Discovery.ProbeAddressAsync(new Proto.ProbeAddressRequest { Address = "10.9.0.1", UserName = "root" }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, half.StatusCode);
    }

    [Fact]
    public async Task CredentialListIsEncryptedAndNeverReturnsPasswords()
    {
        await using var host = await TestServerHost.StartAsync();

        var first = await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = " root ", Password = "list-Secret-1" });
        var duplicate = await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = "list-Secret-1" });
        await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "operator", Password = "list-Secret-2" });
        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal("root", first.UserName);
        Assert.NotNull(first.Created);

        var list = await host.Settings.ListCredentialsAsync(new Proto.Empty());
        Assert.Equal(["root", "operator"], list.Entries.Select(e => e.UserName));

        // Encrypted at rest: the plaintext is nowhere in the database file.
        SqliteConnection.ClearAllPools();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(host.DataDirectory, "oadm.db"));
        Assert.DoesNotContain("list-Secret-1", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        var decrypted = await host.Get<CredentialListStore>().GetAllAsync(CancellationToken.None);
        Assert.Equal("list-Secret-1", decrypted[0].Password);
        Assert.DoesNotContain("list-Secret-1", decrypted[0].ToString(), StringComparison.Ordinal);

        var invalid = await Assert.ThrowsAsync<RpcException>(() => host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "", Password = "x" }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, invalid.StatusCode);
        var badPassword = await Assert.ThrowsAsync<RpcException>(() => host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "root", Password = "" }).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, badPassword.StatusCode);

        await host.Settings.RemoveCredentialAsync(new Proto.CredentialEntryId { Id = first.Id });
        Assert.Equal(["operator"], (await host.Settings.ListCredentialsAsync(new Proto.Empty())).Entries.Select(e => e.UserName));
        var gone = await Assert.ThrowsAsync<RpcException>(() => host.Settings.RemoveCredentialAsync(new Proto.CredentialEntryId { Id = first.Id }).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task RevealCredentialReturnsTheStoredPasswordAndLogsOnlyTheUserName()
    {
        var log = new CapturingLogger<Oadm.Server.Settings.SettingsGrpcService>();
        await using var host = await TestServerHost.StartAsync(configureServices: s => s.AddSingleton<ILogger<Oadm.Server.Settings.SettingsGrpcService>>(log));
        var entry = await host.Settings.AddCredentialAsync(new Proto.AddCredentialRequest { UserName = "service", Password = "reveal-Secret-9" });

        var revealed = await host.Settings.RevealCredentialAsync(new Proto.CredentialEntryId { Id = entry.Id });
        Assert.Equal("reveal-Secret-9", revealed.Password);

        var unknown = await Assert.ThrowsAsync<RpcException>(() => host.Settings.RevealCredentialAsync(new Proto.CredentialEntryId { Id = Guid.NewGuid().ToString() }).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);
        var invalid = await Assert.ThrowsAsync<RpcException>(() => host.Settings.RevealCredentialAsync(new Proto.CredentialEntryId { Id = "not-a-guid" }).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, invalid.StatusCode);

        await host.Settings.RemoveCredentialAsync(new Proto.CredentialEntryId { Id = entry.Id });
        var removed = await Assert.ThrowsAsync<RpcException>(() => host.Settings.RevealCredentialAsync(new Proto.CredentialEntryId { Id = entry.Id }).ResponseAsync);
        Assert.Equal(StatusCode.NotFound, removed.StatusCode);

        // The server log names the user, never the password.
        var revealedEntry = Assert.Single(log.Entries, e => e.Message.Contains("revealed", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, revealedEntry.Level);
        Assert.Equal("Credential list password of service revealed", revealedEntry.Message);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("reveal-Secret-9", StringComparison.Ordinal));
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));
    }

    [Theory]
    [InlineData("10.0.0.48", "10.0.0.48", null, null, "10.0.0.48")]
    [InlineData(" 10.0.0.48:8443 ", "10.0.0.48", 8443, null, "10.0.0.48:8443")]
    [InlineData("https://cam1.example.com", "cam1.example.com", null, "https", "cam1.example.com")]
    [InlineData("http://cam1:8080/", "cam1", 8080, "http", "cam1:8080")]
    [InlineData("fe80::1", "fe80::1", null, null, "fe80::1")]
    [InlineData("[fe80::1]:8443", "fe80::1", 8443, null, "[fe80::1]:8443")]
    [InlineData("axis-accc8e000001.local", "axis-accc8e000001.local", null, null, "axis-accc8e000001.local")]
    public void EnteredAddressesAreParsed(string input, string host, int? port, string? scheme, string deviceAddress)
    {
        Assert.True(EnteredAddress.TryParse(input, out var parsed, out var error), error);
        Assert.Equal(host, parsed!.Host);
        Assert.Equal(port, parsed.Port);
        Assert.Equal(scheme, parsed.Scheme);
        Assert.Equal(deviceAddress, parsed.DeviceAddress);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://cam1")]
    [InlineData("http://user:pw@cam1")]
    [InlineData("cam1/path")]
    [InlineData("not a host")]
    public void InvalidEnteredAddressesAreRejected(string input)
    {
        Assert.False(EnteredAddress.TryParse(input, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }
}
