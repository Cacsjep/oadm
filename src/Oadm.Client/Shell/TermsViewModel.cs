using System.Security.Cryptography;
using System.Text;

using CommunityToolkit.Mvvm.Input;

using Oadm.Client.Infrastructure;
using Oadm.Client.Settings;

namespace Oadm.Client.Shell;

/// <summary>
/// The terms of use on the first start of the client on this computer: Accept or Quit. Accepting stores the version of the
/// terms (SHA-256 of the text) in the client settings; changed terms ask again. The installers do not ask (they skip it
/// when installed silently, and Linux packages never ask), so this is the one place every user passes.
/// </summary>
public sealed partial class TermsViewModel(IClientSettingsStore settings)
{
    public string Title { get; } = AboutViewModel.Terms.Title;

    public string Text { get; } = AboutViewModel.Terms.Body;

    /// <summary>The Apache License 2.0 (LICENSE of the repository): the terms end with "The license terms ... follow".</summary>
    public string License { get; } = ReadLicense();

    /// <summary>The version of the terms this client carries.</summary>
    public static string CurrentVersion { get; } = Version(AboutViewModel.Terms.Title + "\n" + AboutViewModel.Terms.Body);

    /// <summary>True when the user accepted, false when they quit; raised once.</summary>
    public event EventHandler<bool>? Completed;

    public static bool IsAccepted(ClientSettings current)
    {
        ArgumentNullException.ThrowIfNull(current);
        return string.Equals(current.AcceptedTerms, CurrentVersion, StringComparison.Ordinal);
    }

    private static string ReadLicense()
    {
        using var stream = typeof(TermsViewModel).Assembly.GetManifestResourceStream("Oadm.Client.LICENSE")
            ?? throw new InvalidOperationException("LICENSE is not embedded in the client.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    public static string Version(string terms) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(terms.Replace("\r\n", "\n", StringComparison.Ordinal))));

    [RelayCommand]
    private void Accept()
    {
        settings.Current.AcceptedTerms = CurrentVersion;
        settings.Save();
        Completed?.Invoke(this, true);
    }

    [RelayCommand]
    private void Quit() => Completed?.Invoke(this, false);
}
