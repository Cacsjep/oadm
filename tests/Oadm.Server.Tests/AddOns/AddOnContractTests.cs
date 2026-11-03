using Grpc.Core;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Oadm.Core.Auth;
using Oadm.Core.Plugins;
using Oadm.Core.Plugins.AddOns;
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

        var list = await op.ListAddOnsAsync(new Proto.Empty());

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

        var listed = Assert.Single((await host.Plugins.ListAddOnsAsync(new Proto.Empty())).AddOns);
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

        // Remove: off at once, still loaded, the row says when the files go.
        var afterRemove = Assert.Single((await host.Plugins.RemoveAddOnAsync(new Proto.AddOnIdRequest { Id = Id })).AddOns);
        Assert.Equal((Proto.AddOnState.AddOnRemoved, AddOnTexts.Removed), (afterRemove.State, afterRemove.Message));
        Assert.True(registry.HasPackage(Id));
        Assert.False(registry.IsEnabled(Id));

        // Audited as users name it: "optional plugin", never "add-on".
        var audit = (await host.Audit.ListAsync(new Proto.ListAuditRequest())).Entries;
        Assert.Contains(audit, e => e.Action == "Installed optional plugin" && e.Target == $"Text recognition V{AddOnRig.Version}");
        Assert.Contains(audit, e => e.Action == "Removed optional plugin" && e.Target == $"Text recognition V{AddOnRig.Version}");
    }
}
