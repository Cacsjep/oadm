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
/// Remove for administrators, the client part for every signed-in user. A development build downloads nothing; a server
/// of a release (its options and release files faked, never a real download) installs, follows and removes an add-on.
/// </summary>
public sealed class AddOnContractTests
{
    private const string Id = AddOnCatalog.TextRecognitionId;

    [Fact]
    public async Task EverySignedInUserSeesTheAddOnsOfThisVersion()
    {
        await using var host = await TestServerHost.StartAsync();
        var op = new Proto.PluginService.PluginServiceClient(await host.InvokerForUserAsync("operator1", UserRole.Operator));

        var list = await op.ListAddOnsAsync(new Proto.ListAddOnsRequest { CheckReach = true });

        var ocr = Assert.Single(list.AddOns);
        Assert.Equal((Id, "Text recognition", "Reads text in camera pictures for the event rules."), (ocr.Id, ocr.DisplayName, ocr.Description));
        Assert.Equal(Proto.AddOnState.AddOnNotInstalled, ocr.State);
        Assert.False(ocr.CanInstall); // a development build: no release to download from
        Assert.Equal(AddOnTexts.NoRelease, ocr.Message);
    }

    [Fact]
    public async Task InstallAndRemoveAreForAdministratorsAndADevelopmentBuildDownloadsNothing()
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

        var remove = await Assert.ThrowsAsync<RpcException>(async () => await host.Plugins.RemoveAddOnAsync(request));
        Assert.Equal(StatusCode.FailedPrecondition, remove.StatusCode);
        using (var install = host.Plugins.InstallAddOn(request))
        {
            var noRelease = await Assert.ThrowsAsync<RpcException>(async () => await install.ResponseStream.MoveNext(CancellationToken.None));
            Assert.Equal((StatusCode.FailedPrecondition, AddOnTexts.NoRelease), (noRelease.StatusCode, noRelease.Status.Detail));
        }

        var unknown = await Assert.ThrowsAsync<RpcException>(async () => await host.Plugins.GetAddOnClientPartAsync(new Proto.AddOnIdRequest { Id = "nope" }));
        Assert.Equal((StatusCode.NotFound, AddOnTexts.Unknown("nope")), (unknown.StatusCode, unknown.Status.Detail));
        var part = await Assert.ThrowsAsync<RpcException>(async () => await op.GetAddOnClientPartAsync(request));
        Assert.Equal(StatusCode.NotFound, part.StatusCode); // signed in is enough; it is not installed
    }

    [Fact]
    public async Task AReleaseServerInstallsAnAddOnOffFollowsItAndRemovesIt()
    {
        var files = new FakeAddOnFiles();
        files.Publish(AddOnRig.Version, AddOnInstallerOptions.CurrentRuntimeId(), AddOnRig.Zip());
        var dataDirectory = TestServerHost.NewDataDirectory();
        await using var host = await TestServerHost.StartAsync(
            dataDirectory: dataDirectory,
            configureServices: services =>
            {
                services.RemoveAll<AddOnInstallerOptions>();
                services.AddSingleton(new AddOnInstallerOptions(dataDirectory, AddOnRig.Version, AddOnInstallerOptions.CurrentRuntimeId()));
                services.RemoveAll<IAddOnFiles>();
                services.AddSingleton<IAddOnFiles>(files);
            });
        var registry = host.Get<PluginRegistry>();
        Assert.True(host.Get<IAddOnInstaller>() is AddOnInstaller);

        var listed = Assert.Single((await host.Plugins.ListAddOnsAsync(new Proto.ListAddOnsRequest { CheckReach = true })).AddOns);
        Assert.True(listed.CanInstall);

        var steps = new List<Proto.AddOnProgress>();
        using (var install = host.Plugins.InstallAddOn(new Proto.AddOnIdRequest { Id = Id }))
        {
            while (await install.ResponseStream.MoveNext(CancellationToken.None))
            {
                steps.Add(install.ResponseStream.Current);
            }
        }

        Assert.True(steps[^1].Done, string.Join(", ", steps.Select(s => s.Error)));
        Assert.Equal(
            [Proto.AddOnStep.Download, Proto.AddOnStep.Check, Proto.AddOnStep.Install],
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

        // A removed optional plugin cannot be turned on in the plugin list; Install on its card keeps it.
        var turnOn = await Assert.ThrowsAsync<RpcException>(async () => await host.Plugins.SetPackageEnabledAsync(new Proto.SetPackageEnabledRequest { Id = Id, Enabled = true }));
        Assert.Equal((StatusCode.FailedPrecondition, AddOnTexts.Removed), (turnOn.StatusCode, turnOn.Status.Detail));
        Assert.False(registry.IsEnabled(Id));
        Assert.True(afterRemove.CanInstall);

        // Audited as users name it: "optional plugin", never "add-on".
        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == "Installed optional plugin" && e.Target == $"Text recognition V{AddOnRig.Version}");
        Assert.Contains(audit, e => e.Action == "Removed optional plugin" && e.Target == $"Text recognition V{AddOnRig.Version}");
    }

    [Fact]
    public async Task AnInstallIsAuditedWhenTheCallerHasGone()
    {
        var files = new FakeAddOnFiles();
        files.Publish(AddOnRig.Version, AddOnInstallerOptions.CurrentRuntimeId(), AddOnRig.Zip());
        await using var host = await StartReleaseAsync(files);
        var service = ActivatorUtilities.CreateInstance<PluginGrpcService>(host.Services);

        // Every write fails: the caller has gone. The install runs to its end and is audited.
        var gone = new ThrowingStream();
        await service.InstallAddOn(new Proto.AddOnIdRequest { Id = Id }, gone, new BareCallContext());

        Assert.True(gone.Attempts > 0);
        Assert.True(host.Get<PluginRegistry>().HasPackage(Id));
        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.AddOnInstalled && e.Target == $"Text recognition V{AddOnRig.Version}");
    }

    [Fact]
    public async Task AFailedInstallIsAuditedWithItsReason()
    {
        var files = new FakeAddOnFiles { WrongHash = true };
        files.Publish(AddOnRig.Version, AddOnInstallerOptions.CurrentRuntimeId(), AddOnRig.Zip());
        await using var host = await StartReleaseAsync(files);
        var recorded = new RecordingStream();

        await ActivatorUtilities.CreateInstance<PluginGrpcService>(host.Services)
            .InstallAddOn(new Proto.AddOnIdRequest { Id = Id }, recorded, new BareCallContext());

        Assert.Equal(AddOnTexts.Damaged, recorded.Messages[^1].Error);
        Assert.False(host.Get<PluginRegistry>().HasPackage(Id));
        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == AuditActions.AddOnInstallFailed && e.Target == "Text recognition" && e.Detail == AddOnTexts.Damaged);
    }

    /// <summary>A server of a release whose release files are <paramref name="files"/> (never a real download).</summary>
    private static async Task<TestServerHost> StartReleaseAsync(FakeAddOnFiles files)
    {
        var dataDirectory = TestServerHost.NewDataDirectory();
        return await TestServerHost.StartAsync(
            dataDirectory: dataDirectory,
            configureServices: services =>
            {
                services.RemoveAll<AddOnInstallerOptions>();
                services.AddSingleton(new AddOnInstallerOptions(dataDirectory, AddOnRig.Version, AddOnInstallerOptions.CurrentRuntimeId()));
                services.RemoveAll<IAddOnFiles>();
                services.AddSingleton<IAddOnFiles>(files);
            });
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
        protected override string MethodCore => "/oadm.v1.PluginService/InstallAddOn";

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
