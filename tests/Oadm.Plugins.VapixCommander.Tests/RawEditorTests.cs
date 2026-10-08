using Oadm.Plugins.VapixCommander.Client;

namespace Oadm.Plugins.VapixCommander.Tests;

/// <summary>The raw request editor shows the request headers only where they are needed.</summary>
public sealed class RawEditorTests
{
    [Fact]
    public void Headers_are_hidden_for_get_unless_the_request_has_some()
    {
        var raw = new RawEditorViewModel();
        var changes = new List<string?>();
        raw.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.Equal("GET", raw.Method);
        Assert.False(raw.ShowHeaders);

        raw.Method = "POST";
        Assert.True(raw.ShowHeaders);
        Assert.Contains(nameof(RawEditorViewModel.ShowHeaders), changes);

        raw.Method = "GET";
        Assert.False(raw.ShowHeaders);

        // A command that already has headers keeps showing them (never hide data).
        changes.Clear();
        raw.HeaderRows.Add(new KeyValueRowViewModel { Key = "Accept", Value = "application/json" });
        Assert.True(raw.ShowHeaders);
        Assert.Contains(nameof(RawEditorViewModel.ShowHeaders), changes);
    }
}
