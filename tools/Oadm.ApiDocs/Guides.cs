namespace Oadm.ApiDocs;

/// <summary>A guide page of the developer docs ("Task plugins", "dev/tasks/").</summary>
internal sealed record Guide(string Title, string Path);

/// <summary>
/// Hand-kept texts of the reference: one sentence per namespace and the guide page of the main types. A test checks that
/// every type named here still exists.
/// </summary>
internal static class Guides
{
    private static readonly Guide Devices = new("Devices and VAPIX", "dev/devices/");
    private static readonly Guide Tasks = new("Task plugins", "dev/tasks/");
    private static readonly Guide Core = new("Core plugins and pages", "dev/core-plugins/");
    private static readonly Guide Rules = new("Event rules", "dev/event-rules/");
    private static readonly Guide Ui = new("UI kit", "dev/ui/");
    private static readonly Guide Basics = new("Plugin basics", "dev/plugins/");

    public static readonly IReadOnlyDictionary<string, string> Namespaces = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Oadm.Sdk.Actions"] = "Actions of the event rules: what a rule does when it turns on or off.",
        ["Oadm.Sdk.Devices"] = "The managed devices as plugins see them, their status texts and the event streams.",
        ["Oadm.Sdk.Events"] = "Events for the event rules and the rule model.",
        ["Oadm.Sdk.Network"] = "Helpers for plugins that serve the network: interfaces, port errors, rate limits, firewall rules.",
        ["Oadm.Sdk.Plugins"] = "The plugin interfaces and the context the server gives them: task plugins, core plugins, steps, permissions.",
        ["Oadm.Sdk.Tasks"] = "Starting and cancelling tasks from a core plugin.",
        ["Oadm.Sdk.Vapix"] = "The logged-in VAPIX client of a device, API checks, request options and safe XML parsing.",
        ["Oadm.Sdk.Client"] = "The client parts of a plugin: task dialogs, rail pages and toolbar entries, and what the client gives them.",
        ["Oadm.Sdk.Client.Collections"] = "Collections for grids that change in one step.",
        ["Oadm.Sdk.Client.Controls"] = "The shared controls of the OADM client for plugin dialogs and pages.",
        ["Oadm.Sdk.Client.Network"] = "The network interface select of plugin pages.",
        ["Oadm.Sdk.Client.Validation"] = "Form validation with errors directly below the field.",
    };

    public static readonly IReadOnlyDictionary<string, Guide> Types = new Dictionary<string, Guide>(StringComparer.Ordinal)
    {
        ["IDeviceInfo"] = Devices,
        ["IDeviceRepository"] = Devices,
        ["DeviceMessages"] = Devices,
        ["IDeviceEventStreams"] = Devices,
        ["IVapixClient"] = Devices,
        ["IVapixClientFactory"] = Devices,
        ["DeviceApi"] = Devices,
        ["DeviceApiExtensions"] = Devices,
        ["VapixRequestOptions"] = Devices,
        ["DeviceXml"] = Devices,
        ["ITaskPlugin"] = Tasks,
        ["ITaskExecutionContext"] = Tasks,
        ["ITaskStep"] = Tasks,
        ["TaskStepExtensions"] = Tasks,
        ["TaskSupportReasons"] = Tasks,
        ["ITaskPluginQuery"] = Tasks,
        ["ITaskQueryContext"] = Tasks,
        ["IUploadedFiles"] = Tasks,
        ["ITaskPluginDialog"] = Tasks,
        ["ITaskDialogContext"] = Tasks,
        ["PermissionDefinition"] = Tasks,
        ["IPlugin"] = Basics,
        ["PluginManifest"] = Basics,
        ["ICorePlugin"] = Core,
        ["ICorePluginContext"] = Core,
        ["IPluginSettings"] = Core,
        ["IPluginEvents"] = Core,
        ["ISecretProtector"] = Core,
        ["ICorePluginDownloads"] = Core,
        ["ITaskRunner"] = Core,
        ["ICorePluginPage"] = Core,
        ["ICorePluginClientContext"] = Core,
        ["IToolbarPlugin"] = Core,
        ["IToolbarContext"] = Core,
        ["IActionPlugin"] = Rules,
        ["IActionContext"] = Rules,
        ["ActionField"] = Rules,
        ["ActionInvocation"] = Rules,
        ["IActionSource"] = Rules,
        ["IEventSource"] = Rules,
        ["IDynamicEventSource"] = Rules,
        ["IEventPublisher"] = Rules,
        ["EventTypeInfo"] = Rules,
        ["ValidatingViewModel"] = Ui,
        ["FormValidator"] = Ui,
        ["RangeObservableCollection"] = Ui,
        ["MessageWindow"] = Ui,
        ["DevicePickerWindow"] = Ui,
        ["FormField"] = Ui,
        ["StatusChip"] = Ui,
    };

    public static Guide? ForType(string name) => Types.GetValueOrDefault(name);
}
