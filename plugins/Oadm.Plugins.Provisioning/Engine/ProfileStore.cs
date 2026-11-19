using System.Text.Json;

using Oadm.Plugins.Provisioning.Model;
using Oadm.Plugins.Provisioning.Nodes;
using Oadm.Sdk.Plugins;

namespace Oadm.Plugins.Provisioning.Engine;

/// <summary>
/// The profiles and the active one (plugin setting <c>profiles</c>). Settings hold no secret: Set password keeps the id of
/// a credential list entry only. One writer at a time.
/// </summary>
public sealed class ProfileStore(IPluginSettings settings) : IDisposable
{
    public const string ProfilesKey = "profiles";

    private readonly SemaphoreSlim _write = new(1, 1);
    private volatile ProfileBook _book = new();

    public ProfileBook Book => _book;

    /// <summary>The active profile; null when none is.</summary>
    public Profile? Active => _book.ActiveId is { } id ? _book.Profiles.FirstOrDefault(p => p.Id == id) : null;

    public Profile? Find(Guid id) => _book.Profiles.FirstOrDefault(p => p.Id == id);

    /// <summary>Reads the profiles; those of the build of 2026-11-18 are converted (<see cref="ProfileUpgrade"/>) and stored again.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var book = PluginJson.TryParse<ProfileBook>(await settings.GetAsync(ProfilesKey, ct).ConfigureAwait(false), ProvisioningJson.Options) ?? new ProfileBook();
        var changed = false;
        var profiles = book.Profiles.Select(p =>
        {
            var converted = ProfileUpgrade.Convert(p, out var one);
            changed |= one;
            return converted;
        }).ToList();
        _book = book with { Profiles = profiles };
        if (changed)
        {
            await ChangeAsync(b => b, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A new profile with the fixed node and its default settings.</summary>
    public static Profile NewProfile(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Nodes =
        [
            new FlowNode { Id = "set-password", Kind = NodeKind.SetPassword, ContinueOnError = false, Settings = NodeSettings.Write(new SetPasswordSettings()) },
        ],
    };

    /// <summary>
    /// The profile as it is stored: a new id when it has none, nodes of the build of 2026-11-18 converted, Continue on error
    /// off for the nodes that always stop, the name trimmed.
    /// </summary>
    public static Profile Normalize(Profile profile, NodeRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(registry);
        profile = ProfileUpgrade.Convert(profile, out _);
        return profile with
        {
            Id = profile.Id == Guid.Empty ? Guid.NewGuid() : profile.Id,
            Name = profile.Name?.Trim() ?? string.Empty,
            Nodes = [.. (profile.Nodes ?? []).Select(n => registry.Find(n.Kind) is { AlwaysStops: true } ? n with { ContinueOnError = false } : n)],
        };
    }

    /// <summary>Stores <paramref name="profile"/> (added, or replacing the one with its id).</summary>
    public async Task SaveAsync(Profile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await ChangeAsync(book =>
        {
            var list = book.Profiles.ToList();
            var index = list.FindIndex(p => p.Id == profile.Id);
            if (index >= 0)
            {
                list[index] = profile;
            }
            else
            {
                if (list.Count >= ProfileCheck.MaxProfiles)
                {
                    throw new InvalidOperationException($"OADM keeps at most {ProfileCheck.MaxProfiles} profiles.");
                }

                list.Add(profile);
            }

            return book with { Profiles = list };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a profile; the active one leaves no active profile. False when there is none with the id.</summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        var found = false;
        await ChangeAsync(book =>
        {
            found = book.Profiles.Any(p => p.Id == id);
            return book with { Profiles = [.. book.Profiles.Where(p => p.Id != id)], ActiveId = book.ActiveId == id ? null : book.ActiveId };
        }, ct).ConfigureAwait(false);
        return found;
    }

    /// <summary>Makes the profile active (checked by the caller).</summary>
    public Task ActivateAsync(Guid id, CancellationToken ct) => ChangeAsync(book => book with { ActiveId = id }, ct);

    public void Dispose() => _write.Dispose();

    private async Task ChangeAsync(Func<ProfileBook, ProfileBook> change, CancellationToken ct)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var next = change(_book);
            await settings.SetAsync(ProfilesKey, JsonSerializer.Serialize(next, ProvisioningJson.Options), ct).ConfigureAwait(false);
            _book = next;
        }
        finally
        {
            _write.Release();
        }
    }
}
