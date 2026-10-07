using System.Globalization;

using Avalonia.Controls;

using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Controls;

namespace Oadm.TestPlugins.Sample.Client;

/// <summary>
/// Sample toolbar plugin: a shared <see cref="ToolbarButton"/> that shows how many devices are
/// selected and, when clicked, runs the sample task on them. Proves that toolbar plugins load from
/// a <c>*.Client.dll</c> and see the selection.
/// </summary>
public sealed class SampleToolbarPlugin : IToolbarPlugin
{
    public string Id => "oadm.sample.toolbar";

    public int Order => 0;

    public ToolbarGroup Group => ToolbarGroup.Plugins;

    public Control CreateControl(IToolbarContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var button = new ToolbarButton { IconKey = "plugin" };

        void Update()
        {
            int count = ctx.SelectedDevices.Count;
            button.Text = string.Create(CultureInfo.InvariantCulture, $"Sample ({count})");
            button.IsEnabled = count > 0;
        }

        ctx.SelectionChanged += (_, _) => Update();
        button.Click += async (_, _) => await ctx.RunTaskAsync("oadm.sample.standalone", CancellationToken.None).ConfigureAwait(true);
        Update();
        return button;
    }
}
