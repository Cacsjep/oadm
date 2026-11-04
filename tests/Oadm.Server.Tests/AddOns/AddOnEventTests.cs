using Oadm.Core.Events;
using Oadm.Sdk.Events;
using Oadm.Sdk.Testing;
using Oadm.Server.AddOns;

namespace Oadm.Server.Tests.AddOns;

/// <summary>The event "Optional plugin not loaded" (docs/spec/add-ons.md).</summary>
public sealed class AddOnEventTests
{
    private static (AddOnEventSource Source, FakeEventPublisher Publisher, List<string> Names) Rig()
    {
        var publisher = new FakeEventPublisher();
        var names = new List<string>();
        return (new AddOnEventSource(() => names, () => publisher), publisher, names);
    }

    [Fact]
    public void TheEventIsOneStateOfTheServerInTheFolderOptionalPlugins()
    {
        var (source, _, _) = Rig();

        var type = Assert.Single(source.EventTypes);

        Assert.Equal(("Optional plugin not loaded", EventKind.Stateful, EventScope.Server), (type.DisplayName, type.Kind, type.Scope));
        Assert.Equal(["Optional plugins"], type.Folders);
        Assert.Equal("Optional plugins", ((IHostEventSource)source).SourceName);
    }

    [Fact]
    public void TheFirstReportMakesTheTypeReadyWithNothingOn()
    {
        var (source, publisher, _) = Rig();

        source.Report();

        Assert.Empty(publisher.States[AddOnEventSource.NotLoadedTypeId]);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public void APluginThatIsNotLoadedTurnsTheEventOnAtTheStartAndAnInstallTurnsItOff()
    {
        var (source, publisher, names) = Rig();
        names.Add("Text recognition");

        source.Report();
        source.Report();

        var on = Assert.Single(publisher.Published);
        Assert.Equal((true, "1", "Text recognition"), (on.IsOn, on.Data[AddOnEventSource.CountField], on.Data[AddOnEventSource.PluginField]));
        Assert.Equal([Guid.Empty], publisher.States[AddOnEventSource.NotLoadedTypeId]);

        names.Clear();
        source.Report();

        Assert.Empty(publisher.States[AddOnEventSource.NotLoadedTypeId]);
    }
}
