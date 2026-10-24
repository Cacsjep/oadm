using Oadm.Sdk.Devices;

namespace Oadm.Sdk.Testing;

/// <summary>The protected tag names of a plugin in memory (<see cref="IProtectedTags"/>): a test reads what the plugin set.</summary>
public sealed class FakeProtectedTags : IProtectedTags
{
    private readonly Lock _gate = new();
    private HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private Func<string, string>? _refusal;

    /// <summary>The names the plugin protects now.</summary>
    public IReadOnlySet<string> Names
    {
        get
        {
            lock (_gate)
            {
                return _names;
            }
        }
    }

    /// <summary>How often the plugin protected a set of names.</summary>
    public int SetCount { get; private set; }

    /// <inheritdoc />
    public void Protect(IReadOnlyCollection<string> names, Func<string, string> refusal)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(refusal);
        lock (_gate)
        {
            _names = new HashSet<string>(names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()), StringComparer.OrdinalIgnoreCase);
            _refusal = refusal;
            SetCount++;
        }
    }

    /// <summary>The plugin's refusal for <paramref name="name"/>, null when the name is not protected.</summary>
    public string? RefusalFor(string name)
    {
        lock (_gate)
        {
            return _names.Contains(name.Trim()) ? _refusal?.Invoke(name.Trim()) : null;
        }
    }
}
