using System.Security.Cryptography;
using System.Text;

namespace Oadm.Contracts;

/// <summary>
/// What the server and the client agree on about an add-on's client part (docs/spec/add-ons.md rule 6): the folder below the
/// add-on folder that goes to the clients with its dependencies, and the digest of the whole part.
/// </summary>
public static class AddOnLayout
{
    /// <summary>A client part's own folder in the add-on folder: everything below it goes to the clients.</summary>
    public const string ClientFolder = "client";

    /// <summary>
    /// The digest of a client part: SHA-256 (lowercase hex) over one "<c>path sha256</c>" line per file ("/" separated
    /// path relative to the add-on folder, the file's SHA-256 in lowercase hex), sorted by path (ordinal).
    /// </summary>
    public static string Digest(IEnumerable<(string Path, string Sha256)> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var text = new StringBuilder();
        foreach (var (path, sha256) in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            text.Append(path).Append(' ').Append(sha256.ToLowerInvariant()).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
