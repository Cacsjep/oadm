using Avalonia.Controls;

using Oadm.Plugins.Ocr.Api;
using Oadm.Sdk.Client;

namespace Oadm.Plugins.Ocr.Client;

/// <summary>Navigation rail page of the "Text recognition" core plugin (docs/spec/ocr.md "Page"): four cards, readers, video, results and settings.</summary>
public sealed class OcrPage : ICorePluginPage
{
    public string PluginId => OcrInfo.PluginId;

    public string Title => OcrInfo.DisplayName;

    public bool HasOwnCards => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new OcrView { DataContext = new OcrViewModel(ctx) };
    }
}
