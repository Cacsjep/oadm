namespace Oadm.Core.Settings;

/// <summary>One server setting (row of the Settings table). Value is JSON.</summary>
public sealed class Setting
{
    public string Key { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "null";
}
