using Grpc.Core;

using Oadm.Core.Plugins.AddOns;
using Oadm.Sdk.Plugins;
using Oadm.Server.Tests.Support;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Tests.AddOns;

/// <summary>
/// The add-on methods of PluginService before the installer is built (docs/spec/add-ons.md "Build plan", wave 1): the list
/// from the catalogue for every user, Install and Remove for administrators only and UNIMPLEMENTED until then.
/// </summary>
public sealed class AddOnContractTests
{
    [Fact]
    public async Task EverySignedInUserSeesTheAddOnsOfThisVersion()
    {
        await using var host = await TestServerHost.StartAsync();
        var op = new Proto.PluginService.PluginServiceClient(await host.InvokerForUserAsync("operator1", UserRole.Operator));

        var list = await op.ListAddOnsAsync(new Proto.Empty());

        var ocr = Assert.Single(list.AddOns);
        Assert.Equal((AddOnCatalog.TextRecognitionId, "Text recognition", "Reads text in camera pictures for the event rules."), (ocr.Id, ocr.DisplayName, ocr.Description));
        Assert.Equal(Proto.AddOnState.AddOnNotInstalled, ocr.State);
        Assert.False(ocr.CanInstall);
    }

    [Fact]
    public async Task InstallAndRemoveAreForAdministratorsAndNotBuiltYet()
    {
        await using var host = await TestServerHost.StartAsync();
        var op = new Proto.PluginService.PluginServiceClient(await host.InvokerForUserAsync("operator1", UserRole.Operator));
        var request = new Proto.AddOnIdRequest { Id = AddOnCatalog.TextRecognitionId };

        var denied = await Assert.ThrowsAsync<RpcException>(async () => await op.RemoveAddOnAsync(request));
        Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);
        using (var install = op.InstallAddOn(request))
        {
            var refused = await Assert.ThrowsAsync<RpcException>(async () => await install.ResponseStream.MoveNext(CancellationToken.None));
            Assert.Equal(StatusCode.PermissionDenied, refused.StatusCode);
        }

        var remove = await Assert.ThrowsAsync<RpcException>(async () => await host.Plugins.RemoveAddOnAsync(request));
        Assert.Equal(StatusCode.Unimplemented, remove.StatusCode);
        using (var install = host.Plugins.InstallAddOn(request))
        {
            var notBuilt = await Assert.ThrowsAsync<RpcException>(async () => await install.ResponseStream.MoveNext(CancellationToken.None));
            Assert.Equal(StatusCode.Unimplemented, notBuilt.StatusCode);
        }

        var unknown = await Assert.ThrowsAsync<RpcException>(async () => await host.Plugins.GetAddOnClientPartAsync(new Proto.AddOnIdRequest { Id = "nope" }));
        Assert.Equal(StatusCode.NotFound, unknown.StatusCode);
        var part = await Assert.ThrowsAsync<RpcException>(async () => await op.GetAddOnClientPartAsync(request));
        Assert.Equal(StatusCode.Unimplemented, part.StatusCode); // signed in is enough, the installer is not built
    }
}
