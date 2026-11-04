using Google.Protobuf;

using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Sdk.Plugins;
using Oadm.Server.AddOns;
using Oadm.Server.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.AddOns;

/// <summary>
/// The add-on methods of PluginService on the wired server (docs/spec/add-ons.md): the list for every user, Install and
/// Remove for administrators, the client part for every signed-in user. A development build has no hash and refuses; a server
/// with the hash injected (no real release) takes the ZIP over the stream, installs, follows and removes an add-on.
/// </summary>
public sealed class AddOnContractTests
{
    private const string Id = AddOnCatalog.TextRecognitionId;

    private static readonly string FileName = AddOnCatalog.ReleaseFileName(Id, AddOnRig.Version, AddOnInstallerOptions.CurrentRuntimeId());

    [Fact]
    public async Task EverySignedInUserSeesTheAddOnsOfThisVersion()
    {
        await using var host = await TestServerHost.StartAsync();
        var op = new Proto.PluginService.PluginServiceClient(await host.InvokerForUserAsync("operator1", UserRole.Operator));

        var list = await op.ListAddOnsAsync(new Proto.ListAddOnsRequest());

        var ocr = Assert.Single(list.AddOns);
        Assert.Equal((Id, "Text recognition", "Reads text in camera pictures."), (ocr.Id, ocr.DisplayName, ocr.Description));
        Assert.Equal(Proto.AddOnState.AddOnNotInstalled, ocr.State);
        Assert.False(ocr.CanInstall); // a development build: no hash built in
        Assert.Equal(AddOnTexts.NoHash, ocr.Message);
        Assert.Empty(ocr.DownloadUrl);
    }

    [Fact]
    public async Task InstallAndRemoveAreForAdministratorsAndABuildWithoutTheHashRefuses()
    {
        await using var host = await TestServerHost.StartAsync();
        var op = new Proto.PluginService.PluginServiceClient(await host.InvokerForUserAsync("operator1", UserRole.Operator));
        var request = new Proto.AddOnIdRequest { Id = Id };

        var denied = await Assert.ThrowsAsync<RpcException>(async () => await op.RemoveAddOnAsync(request));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
        using (var install = op.InstallAddOn(request))
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await install.ResponseStream.MoveNext(CancellationToken.None));
            Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        }

        var uploadDenied = await Assert.ThrowsAsync<RpcException>(() => UploadAsync(op, Id, FileName, [1, 2, 3]));
        Assert.Equal(StatusCode.PermissionDenied, uploadDenied.StatusCode);

        var remove = await Assert.ThrowsAsync<RpcException>(async () => await host.Plugins.RemoveAddOnAsync(request));
        Assert.Equal(StatusCode.FailedPrecondition, remove.StatusCode);
        var noHash = await Assert.ThrowsAsync<RpcException>(() => UploadAsync(host.Plugins, Id, FileName, [1, 2, 3]));
        Assert.Equal((StatusCode.FailedPrecondition, AddOnTexts.NoHash), (noHash.StatusCode, noHash.Status.Detail));

        var unknown = await Assert.ThrowsAsync<RpcException>(async () => await host.Plugins.GetAddOnClientPartAsync(new Proto.AddOnIdRequest { Id = "nope" }));
        Assert.Equal((StatusCode.NotFound, AddOnTexts.Unknown("nope")), (unknown.StatusCode, unknown.Status.Detail));
        var unknownUpload = await Assert.ThrowsAsync<RpcException>(() => UploadAsync(host.Plugins, "nope", FileName, [1, 2, 3]));
        Assert.Equal(StatusCode.NotFound, unknownUpload.StatusCode);
        var part = await Assert.ThrowsAsync<RpcException>(async () => await op.GetAddOnClientPartAsync(request));
        Assert.Equal(StatusCode.NotFound, part.StatusCode); // signed in is enough; it is not installed
    }

    [Fact]
    public async Task AnUploadStartsWithAHeader()
    {
        await using var host = await TestServerHost.StartAsync();

        using var call = host.Plugins.UploadAddOn();
        await call.RequestStream.WriteAsync(new Proto.AddOnUploadChunk { Data = ByteString.CopyFrom(1, 2, 3) });
        await call.RequestStream.CompleteAsync();
        var refused = await Assert.ThrowsAsync<RpcException>(async () => await call.ResponseStream.MoveNext(CancellationToken.None));

        Assert.Equal(StatusCode.InvalidArgument, refused.StatusCode);
    }

    [Fact]
    public async Task AServerWithTheHashTakesTheZipInstallsItOffFollowsItAndRemovesIt()
    {
        var zip = AddOnRig.Zip();
        await using var host = await StartReleaseAsync(zip);
        var registry = host.Get<PluginRegistry>();
        Assert.True(host.Get<IAddOnInstaller>() is AddOnInstaller);

        var listed = Assert.Single((await host.Plugins.ListAddOnsAsync(new Proto.ListAddOnsRequest())).AddOns);
        Assert.True(listed.CanInstall);
        Assert.Equal(AddOnCatalog.DownloadUrl(Id, AddOnRig.Version, AddOnInstallerOptions.CurrentRuntimeId()), listed.DownloadUrl);

        var steps = await UploadAsync(host.Plugins, Id, FileName, zip);

        Assert.True(steps[^1].Done, string.Join(", ", steps.Select(s => s.Error)));
        Assert.Equal(
            [Proto.AddOnStep.Upload, Proto.AddOnStep.Check, Proto.AddOnStep.Install],
            steps.Select(s => s.Step).Distinct());

        // Loaded and off; Done came after the server followed the package (the roles know its permissions).
        Assert.True(registry.HasPackage(Id));
        Assert.False(registry.IsEnabled(Id));
        Assert.True(host.Get<PluginActivation>().AddedPackagesFollowed.IsCompleted);
        Assert.Equal(PermissionCatalog.Build(registry).Entries.Select(e => e.Key), host.Get<RoleStore>().Catalog.Entries.Select(e => e.Key));
        var package = (await host.Plugins.ListPackagesAsync(new Proto.Empty())).Packages.Single(p => p.Id == Id);
        Assert.False(package.Enabled);
        var stored = Assert.Single(await host.Get<IAddOnListStore>().ReadAsync(CancellationToken.None));
        Assert.Equal(new InstalledAddOn(Id, AddOnRig.Version), stored);

        // The client part: the client dll, the managed server part it references, client/; never natives or models.
        var part = await host.Plugins.GetAddOnClientPartAsync(new Proto.AddOnIdRequest { Id = Id });
        Assert.Equal(AddOnRig.Version, part.Version);
        Assert.Contains(part.Files, f => f.Path == "Oadm.Plugins.Ocr.Server.dll");
        Assert.DoesNotContain(part.Files, f => f.Path.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase) || f.Path.StartsWith("models/", StringComparison.Ordinal));

        // The list carries the digest of the client part: a client checks its cached copy against it.
        var installedRow = Assert.Single((await host.Plugins.ListAddOnsAsync(new Proto.ListAddOnsRequest())).AddOns);
        Assert.Equal(64, part.Digest.Length);
        Assert.Equal(part.Digest, installedRow.ClientDigest);

        // Remove: off at once, still loaded, the row says when the files go.
        var afterRemove = Assert.Single((await host.Plugins.RemoveAddOnAsync(new Proto.AddOnIdRequest { Id = Id })).AddOns);
        Assert.Equal((Proto.AddOnState.AddOnRemoved, AddOnTexts.Removed), (afterRemove.State, afterRemove.Message));
        Assert.True(registry.HasPackage(Id));
        Assert.False(registry.IsEnabled(Id));

        // A removed optional plugin cannot be turned on in the plugin list; Install on its card keeps it, without a file.
        var turnOn = await Assert.ThrowsAsync<RpcException>(async () => await host.Plugins.SetPackageEnabledAsync(new Proto.SetPackageEnabledRequest { Id = Id, Enabled = true }));
        Assert.Equal((StatusCode.FailedPrecondition, AddOnTexts.Removed), (turnOn.StatusCode, turnOn.Status.Detail));
        Assert.False(registry.IsEnabled(Id));
        Assert.True(afterRemove.CanInstall);
        var kept = new List<Proto.AddOnProgress>();
        using (var install = host.Plugins.InstallAddOn(new Proto.AddOnIdRequest { Id = Id }))
        {
            while (await install.ResponseStream.MoveNext(CancellationToken.None))
            {
                kept.Add(install.ResponseStream.Current);
            }
        }

        Assert.True(kept[^1].Done);
        Assert.Equal(Proto.AddOnState.AddOnInstalled, Assert.Single((await host.Plugins.ListAddOnsAsync(new Proto.ListAddOnsRequest())).AddOns).State);

        // Audited as users name it: "optional plugin", never "add-on".
        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == "Installed optional plugin" && e.Target == $"Text recognition V{AddOnRig.Version}");
        Assert.Contains(audit, e => e.Action == "Removed optional plugin" && e.Target == $"Text recognition V{AddOnRig.Version}");
    }

    [Fact]
    public async Task ATamperedZipIsRefusedInTheStreamAndAudited()
    {
        var zip = AddOnRig.Zip();
        await using var host = await StartReleaseAsync(zip);
        var tampered = AddOnRig.Zip(extraBytes: 10);

        var steps = await UploadAsync(host.Plugins, Id, FileName, tampered);

        Assert.Equal(AddOnTexts.WrongFile, steps[^1].Error);
        Assert.False(host.Get<PluginRegistry>().HasPackage(Id));
        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.AddOnInstallFailed && e.Target == "Text recognition" && e.Detail == AddOnTexts.WrongFile);
    }

    [Fact]
    public async Task AnAddOnThatIsNotLoadedHasItsOnChoiceStoredOff()
    {
        await using var host = await TestServerHost.StartAsync();
        var registry = host.Get<PluginRegistry>();
        const string notLoaded = "oadm.not-loaded";
        registry.SetEnabled(notLoaded, true);

        await new PluginActivationAddOns(host.Get<PluginActivation>(), registry).TurnOffAsync(notLoaded, CancellationToken.None);

        Assert.False(registry.ChoiceOf(notLoaded));
    }

    [Fact]
    public async Task AFileOfAnotherVersionIsRefusedBeforeTheClientSendsAll()
    {
        var zip = AddOnRig.Zip();
        await using var host = await StartReleaseAsync(zip);

        // A released system, not this machine's: a name with an unreleased one (osx-arm64 runners) is left to the hash.
        var steps = await UploadAsync(host.Plugins, Id, AddOnCatalog.ReleaseFileName(Id, "4.10.0", "win-x64"), zip);

        Assert.Equal(AddOnTexts.WrongVersion(AddOnRig.Version), Assert.Single(steps).Error);
    }

    [Fact]
    public async Task AnInstallIsAuditedWhenTheCallerHasGone()
    {
        var zip = AddOnRig.Zip();
        await using var host = await StartReleaseAsync(zip);
        var service = ActivatorUtilities.CreateInstance<PluginGrpcService>(host.Services);

        // Every write fails: the caller has gone after its upload. The install runs to its end and is audited.
        var gone = new ThrowingStream();
        await service.UploadAddOn(new ChunkReader(FileName, zip), gone, new BareCallContext());

        Assert.True(gone.Attempts > 0);
        Assert.True(host.Get<PluginRegistry>().HasPackage(Id));
        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.AddOnInstalled && e.Target == $"Text recognition V{AddOnRig.Version}");
    }

    [Fact]
    public async Task AFailedInstallIsAuditedWithItsReason()
    {
        await using var host = await StartReleaseAsync(AddOnRig.Zip());
        var recorded = new RecordingStream();

        await ActivatorUtilities.CreateInstance<PluginGrpcService>(host.Services)
            .UploadAddOn(new ChunkReader(FileName, AddOnRig.Zip(extraBytes: 10)), recorded, new BareCallContext());

        Assert.Equal(AddOnTexts.WrongFile, recorded.Messages[^1].Error);
        Assert.False(host.Get<PluginRegistry>().HasPackage(Id));
        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.AddOnInstallFailed && e.Target == "Text recognition" && e.Detail == AddOnTexts.WrongFile);
    }

    [Fact]
    public async Task AConnectionThatBreaksIsAnUploadThatStopped()
    {
        await using var host = await StartReleaseAsync(AddOnRig.Zip());
        var recorded = new RecordingStream();

        await ActivatorUtilities.CreateInstance<PluginGrpcService>(host.Services)
            .UploadAddOn(new BrokenReader(FileName), recorded, new BareCallContext());

        Assert.Equal(AddOnTexts.UploadStopped, recorded.Messages[^1].Error);
        Assert.False(host.Get<PluginRegistry>().HasPackage(Id));
    }

    /// <summary>Header, chunks, then every answer; a server that ended the call early is no failure of the sender.</summary>
    private static async Task<List<Proto.AddOnProgress>> UploadAsync(Proto.PluginService.PluginServiceClient client, string id, string fileName, byte[] zip)
    {
        using var call = client.UploadAddOn();
        try
        {
            await foreach (var chunk in ChunksOf(id, fileName, zip))
            {
                await call.RequestStream.WriteAsync(chunk);
            }

            await call.RequestStream.CompleteAsync();
        }
        catch (Exception ex) when (ex is RpcException or InvalidOperationException)
        {
            // The answers below say why.
        }

        var steps = new List<Proto.AddOnProgress>();
        while (await call.ResponseStream.MoveNext(CancellationToken.None))
        {
            steps.Add(call.ResponseStream.Current);
        }

        return steps;
    }

    private static async IAsyncEnumerable<Proto.AddOnUploadChunk> ChunksOf(string id, string fileName, byte[] zip)
    {
        yield return new Proto.AddOnUploadChunk { Header = new Proto.AddOnUploadHeader { Id = id, FileName = fileName, Size = zip.Length } };
        await foreach (var data in AddOnRig.ChunksOf(zip))
        {
            yield return new Proto.AddOnUploadChunk { Data = ByteString.CopyFrom(data.Span) };
        }
    }

    /// <summary>A server of version <see cref="AddOnRig.Version"/> with the hash of <paramref name="zip"/> (no real release).</summary>
    private static async Task<TestServerHost> StartReleaseAsync(byte[] zip)
    {
        var hashes = new FakeAddOnHashes();
        hashes.Allow(zip);
        var dataDirectory = TestServerHost.NewDataDirectory();
        return await TestServerHost.StartAsync(
            dataDirectory: dataDirectory,
            configureServices: services =>
            {
                services.RemoveAll<AddOnInstallerOptions>();
                services.AddSingleton(new AddOnInstallerOptions(dataDirectory, AddOnRig.Version, AddOnInstallerOptions.CurrentRuntimeId()));
                services.RemoveAll<IAddOnHashes>();
                services.AddSingleton<IAddOnHashes>(hashes);
            });
    }

    /// <summary>The request stream of a client that sends a whole ZIP.</summary>
    private sealed class ChunkReader(string fileName, byte[] zip) : IAsyncStreamReader<Proto.AddOnUploadChunk>
    {
        private readonly IAsyncEnumerator<Proto.AddOnUploadChunk> _chunks = ChunksOf(Id, fileName, zip).GetAsyncEnumerator();

        public Proto.AddOnUploadChunk Current => _chunks.Current;

        public async Task<bool> MoveNext(CancellationToken cancellationToken) => await _chunks.MoveNextAsync();
    }

    /// <summary>The request stream of a client whose connection breaks after the header.</summary>
    private sealed class BrokenReader(string fileName) : IAsyncStreamReader<Proto.AddOnUploadChunk>
    {
        private bool _headerRead;

        public Proto.AddOnUploadChunk Current { get; } = new() { Header = new Proto.AddOnUploadHeader { Id = Id, FileName = fileName, Size = 1000 } };

        public Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_headerRead)
            {
                return Task.FromException<bool>(new IOException("The client reset the request stream."));
            }

            _headerRead = true;
            return Task.FromResult(true);
        }
    }

    /// <summary>A response stream whose caller has gone: every write fails.</summary>
    private sealed class ThrowingStream : IServerStreamWriter<Proto.AddOnProgress>
    {
        public int Attempts;

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(Proto.AddOnProgress message)
        {
            Interlocked.Increment(ref Attempts);
            return Task.FromException(new InvalidOperationException("The call is complete."));
        }
    }

    private sealed class RecordingStream : IServerStreamWriter<Proto.AddOnProgress>
    {
        public List<Proto.AddOnProgress> Messages { get; } = [];

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(Proto.AddOnProgress message)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>A call context without a caller, deadline or cancellation (the service called directly).</summary>
    private sealed class BareCallContext : ServerCallContext
    {
        protected override string MethodCore => "/oadm.v1.PluginService/UploadAddOn";

        protected override string HostCore => "localhost";

        protected override string PeerCore => "ipv4:127.0.0.1:1";

        protected override DateTime DeadlineCore => DateTime.MaxValue;

        protected override Metadata RequestHeadersCore { get; } = [];

        protected override CancellationToken CancellationTokenCore => CancellationToken.None;

        protected override Metadata ResponseTrailersCore { get; } = [];

        protected override Status StatusCore { get; set; }

        protected override WriteOptions? WriteOptionsCore { get; set; }

        protected override AuthContext AuthContextCore { get; } = new(null, []);

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
