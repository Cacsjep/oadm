namespace Oadm.Plugins.Acap;

/// <summary>The permission keys of Applications (ACAP) ("task:oadm.acap:&lt;key&gt;"); the dialog hides what the role lacks.</summary>
public static class AcapPermissions
{
    public const string Install = "install";
    public const string Downgrade = "downgrade";
    public const string StartStop = "startStop";
    public const string Remove = "remove";
}
