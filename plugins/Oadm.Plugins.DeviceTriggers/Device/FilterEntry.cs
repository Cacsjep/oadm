namespace Oadm.Plugins.DeviceTriggers.Device;

/// <summary>
/// One entry of the filter list: a topic, its value filter (null = the whole topic) and the triggers it serves.
/// <see cref="Key"/> tells entries apart when the device refuses one.
/// </summary>
public sealed record FilterEntry(string Topic, string? ContentFilter, IReadOnlyList<TriggerDefinition> Triggers)
{
    public string Key => Topic + "\n" + ContentFilter;
}
