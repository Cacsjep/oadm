using System.Reflection;

namespace Oadm.Server.AddOns;

/// <summary>
/// The SHA-256 of the genuine add-on ZIP of this server's version and system (docs/spec/add-ons.md rule 2). Only a file with
/// this hash installs. Tests pass their own hashes.
/// </summary>
public interface IAddOnHashes
{
    /// <summary>The lowercase hex SHA-256 of the add-on's ZIP; null for a build without it.</summary>
    string? Find(string id);

    /// <summary>What the ZIP of this version was built from (its <c>oadm-content.txt</c>); null for a build without it.</summary>
    string? FindContent(string id) => null;
}

/// <summary>
/// The hashes built into this assembly: one <see cref="AssemblyMetadataAttribute"/> per add-on, key
/// <see cref="MetadataPrefix"/> + id, from the file <c>manage package addons</c> writes (MSBuild property
/// <c>OadmAddOnHashFile</c> of Oadm.Server.csproj). Metadata of the assembly itself, so it survives PublishSingleFile; nothing
/// is read from a file next to the exe. A value that is no SHA-256 counts as none.
/// </summary>
public sealed class AssemblyAddOnHashes : IAddOnHashes
{
    /// <summary>The start of the metadata key; <c>packaging/addons/check-server-hash.sh</c> looks for it in the published exe.</summary>
    public const string MetadataPrefix = "OadmAddOnSha256:";

    /// <summary>The start of the metadata key of the fingerprint.</summary>
    public const string ContentPrefix = "OadmAddOnContent:";

    private readonly Dictionary<string, string> _hashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _contents = new(StringComparer.OrdinalIgnoreCase);

    public AssemblyAddOnHashes(Assembly? assembly = null)
    {
        foreach (var metadata in (assembly ?? typeof(AssemblyAddOnHashes).Assembly).GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (metadata.Key.StartsWith(MetadataPrefix, StringComparison.Ordinal) && IsHash(metadata.Value))
            {
                _hashes[metadata.Key[MetadataPrefix.Length..]] = metadata.Value!.ToLowerInvariant();
            }
            else if (metadata.Key.StartsWith(ContentPrefix, StringComparison.Ordinal) && IsHash(metadata.Value))
            {
                _contents[metadata.Key[ContentPrefix.Length..]] = metadata.Value!.ToLowerInvariant();
            }
        }
    }

    public string? Find(string id) => _hashes.GetValueOrDefault(id ?? string.Empty);

    public string? FindContent(string id) => _contents.GetValueOrDefault(id ?? string.Empty);

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
