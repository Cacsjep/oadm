using Avalonia.Controls;

using Oadm.Plugins.Vision.Api;
using Oadm.Sdk.Client;

namespace Oadm.Plugins.Vision.Client;

/// <summary>
/// Navigation rail page of "Vision Center" (docs/spec/vision-center.md "Page"): pipelines and models | the node canvas | the
/// live video above the selected node.
/// </summary>
public sealed class VisionPage : ICorePluginPage
{
    public string PluginId => VisionInfo.PluginId;

    public string Title => VisionInfo.DisplayName;

    public bool HasOwnCards => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new VisionView { DataContext = new VisionViewModel(ctx) };
    }
}
