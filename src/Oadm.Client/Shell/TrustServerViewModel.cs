using CommunityToolkit.Mvvm.ComponentModel;

namespace Oadm.Client.Shell;

/// <summary>
/// "Trust this server?" on the first connection to a server (trust on first use): the address, one line on what trusting
/// means, and the certificate's fingerprint behind Fingerprint (the server writes it to its log at startup).
/// </summary>
public sealed partial class TrustServerViewModel(string address, string fingerprint) : ObservableObject
{
    public const string Explanation = "Trust it only if this is your OADM server. OADM then remembers it and warns when its certificate changes.";

    public string Subtitle { get; } = "First connection to " + address;

    /// <summary>The SHA-256 fingerprint in two lines of 16 pairs.</summary>
    public string FingerprintLines { get; } = LoginViewModel.InTwoLines(fingerprint);

    [ObservableProperty]
    public partial bool IsFingerprintOpen { get; set; }
}
