using Avalonia.Controls;

using Oadm.Sdk.Client;

namespace Oadm.Plugins.ActionRules.Client;

/// <summary>Navigation rail page of the "Action Rule Editor" core plugin: the rules card, the tasks pane below.</summary>
public sealed class ActionRulesPage : ICorePluginPage
{
    public string PluginId => ActionRulesInfo.PluginId;

    public string Title => ActionRulesInfo.DisplayName;

    public bool HasOwnCards => true;

    /// <summary>Every change is a task per device: their progress shows below the rules.</summary>
    public bool ShowTasksPane => true;

    public Control CreateView(ICorePluginClientContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return new ActionRulesView { DataContext = new ActionRulesViewModel(ctx) };
    }
}
