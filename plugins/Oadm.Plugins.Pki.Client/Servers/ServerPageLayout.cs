namespace Oadm.Plugins.Pki.Client.Servers;

/// <summary>
/// The two columns of the server tabs (802.1X server, EST server; decision 2026-10-25): the same left column on both (the
/// settings cards), the table of the tab right of it; one column below <see cref="TwoColumnWidth"/>.
/// </summary>
public static class ServerPageLayout
{
    /// <summary>The left column: wide enough for the EST profile rows and the 802.1X switches.</summary>
    public const double LeftColumnWidth = 680;

    /// <summary>From this width of the page the two columns stand side by side.</summary>
    public const double TwoColumnWidth = 1400;
}
