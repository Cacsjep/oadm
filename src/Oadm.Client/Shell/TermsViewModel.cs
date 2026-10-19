using Oadm.Client.Infrastructure;
using Oadm.Client.Settings;
using Oadm.Theme.Shell;

namespace Oadm.Client.Shell;

/// <summary>
/// The terms of use on the first start of the client on this computer: Accept or Quit (the shared
/// <see cref="TermsWindow"/>). Accepting stores the version of the terms (SHA-256 of the text) in the client settings;
/// changed terms ask again. The installers do not ask (they skip it when installed silently, and Linux packages never
/// ask), so this is the one place every user passes.
/// </summary>
public sealed class TermsViewModel(IClientSettingsStore settings)
    : TermsDialogViewModel(AboutViewModel.Terms.Title, AboutViewModel.Terms.Body, TermsText.Resource(typeof(TermsViewModel).Assembly, "Oadm.Client.LICENSE"))
{
    /// <summary>The version of the terms this client carries.</summary>
    public static string CurrentVersion { get; } = Version(AboutViewModel.Terms.Title + "\n" + AboutViewModel.Terms.Body);

    public static bool IsAccepted(ClientSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return string.Equals(current.AcceptedTerms, CurrentVersion, StringComparison.Ordinal);
    }

    public static string Version(string terms) => TermsText.Version(terms);

    protected override void OnAccepted()
    {
        settings.Current.AcceptedTerms = CurrentVersion;
        settings.Save();
    }
}
