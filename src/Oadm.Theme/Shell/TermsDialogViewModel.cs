using System.Security.Cryptography;
using System.Text;

using CommunityToolkit.Mvvm.Input;

namespace Oadm.Theme.Shell;

/// <summary>
/// The terms of use on the first start of the client and of the OADM Site Node app: Accept or Quit. Each app stores the
/// version it accepted (<see cref="TermsText.Version"/>) in its own settings; changed terms ask again.
/// </summary>
public abstract partial class TermsDialogViewModel(string title, string text, string license)
{
    public string Title { get; } = title;

    public string Text { get; } = text;

    /// <summary>The Apache License 2.0: the terms end with "The license terms ... follow".</summary>
    public string License { get; } = license;

    /// <summary>True when the user accepted, false when they quit; raised once.</summary>
    public event EventHandler<bool>? Completed;

    /// <summary>Stores the accepted version (the app's settings).</summary>
    protected abstract void OnAccepted();

    [RelayCommand]
    private void Accept()
    {
        OnAccepted();
        Completed?.Invoke(this, true);
    }

    [RelayCommand]
    private void Quit() => Completed?.Invoke(this, false);
}

/// <summary>TERMS.md as both apps show it.</summary>
public static class TermsText
{
    /// <summary>The first line is the title, the rest the text.</summary>
    public static (string Title, string Body) Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var parts = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Split('\n', 2);
        return (parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : string.Empty);
    }

    /// <summary>The version of a text: SHA-256 hex, line ends ignored.</summary>
    public static string Version(string terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(terms.Replace("\r\n", "\n", StringComparison.Ordinal))));
    }

    /// <summary>An embedded text resource, line ends normalized and trimmed.</summary>
    public static string Resource(System.Reflection.Assembly assembly, string name)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException(name + " is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }
}
