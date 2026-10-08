using System.Diagnostics;

using Oadm.Plugins.MetadataMonitor.Client;
using Oadm.Plugins.MetadataMonitor.Parsing;

using Xunit.Abstractions;

namespace Oadm.Plugins.MetadataMonitor.Tests;

/// <summary>
/// Scale: 10,000 kept messages, a burst of 2,000 in one batch (a camera's Initialized flood), live filtering and the
/// server's parsing of 2,000 documents. Generous budgets; the measured times go to the test output.
/// </summary>
[Trait("Category", "Perf")]
public sealed class ScaleTests(ITestOutputHelper output)
{
    [Fact]
    public void Ten_thousand_messages_and_a_burst_of_two_thousand_stay_fast()
    {
        var ctx = new PluginPageContext(new MetadataMonitorPlugin(), new Oadm.Core.Plugins.PluginEventHub());
        using var vm = new MetadataMonitorViewModel(ctx, new MetadataClientSettingsStore(Path.Combine(Path.GetTempPath(), "oadm-mm-tests", Guid.NewGuid().ToString("N"), "client.json")));
        var events = 0;
        vm.Messages.CollectionChanged += (_, _) => events++;

        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            vm.ApplyMessages(PageViewModelTests.Messages((i * 500) + 1, 500, i % 2 == 0 ? "Device/IO/VirtualInput" : "Storage/Alert"));
        }

        var fill = watch.Elapsed;
        vm.SelectedMessage = vm.Messages[9_000];
        events = 0;
        watch.Restart();
        vm.ApplyMessages(PageViewModelTests.Messages(10_001, 2_000, "Device/IO/VirtualInput"));
        var burst = watch.Elapsed;
        Assert.Equal(MetadataMonitorPluginInfo.MaxClientMessages, vm.Messages.Count);
        Assert.Equal(2, events); // trim + append
        Assert.NotNull(vm.SelectedMessage);

        watch.Restart();
        vm.FilterText = "storage/alert";
        var filter = watch.Elapsed;
        Assert.Equal(4_000, vm.Messages.Count);
        watch.Restart();
        vm.FilterText = "port = 11999;";
        var filterXmlAndInfo = watch.Elapsed;
        Assert.Single(vm.Messages);
        watch.Restart();
        vm.FilterText = string.Empty;
        var clearFilter = watch.Elapsed;
        Assert.Equal(10_000, vm.Messages.Count);

        output.WriteLine($"fill 10,000 in 20 batches: {fill.TotalMilliseconds:F1} ms; burst 2,000: {burst.TotalMilliseconds:F1} ms; filter: {filter.TotalMilliseconds:F1} ms, {filterXmlAndInfo.TotalMilliseconds:F1} ms; clear filter: {clearFilter.TotalMilliseconds:F1} ms");
        Assert.True(burst < TimeSpan.FromMilliseconds(200), $"burst {burst.TotalMilliseconds} ms");
        Assert.True(filter < TimeSpan.FromMilliseconds(200), $"filter {filter.TotalMilliseconds} ms");
        Assert.True(filterXmlAndInfo < TimeSpan.FromMilliseconds(200), $"filter {filterXmlAndInfo.TotalMilliseconds} ms");
    }

    [Fact]
    public void Server_parses_a_burst_of_two_thousand_documents_quickly()
    {
        var documents = Enumerable.Range(1, 2_000)
            .Select(i => RecordedEvents.Notification("tns1:Device/tnsaxis:IO/VirtualInput", "Initialized", i, 0))
            .ToList();
        var watch = Stopwatch.StartNew();
        var messages = documents.Sum(d => MetadataParser.Parse(d).Count);
        var elapsed = watch.Elapsed;

        output.WriteLine($"parsed 2,000 documents in {elapsed.TotalMilliseconds:F1} ms");
        Assert.Equal(2_000, messages);
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"{elapsed.TotalMilliseconds} ms");
    }
}
