using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Core.Settings;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Devices;

/// <summary>
/// The tag names core plugins protect (<see cref="IProtectedTags"/>, docs/spec/tags.md "Protected tags"): only users with
/// all devices add them to or remove them from devices. <see cref="Plugins.CorePluginHost"/> hands each plugin its view
/// (<see cref="For"/>); <see cref="DeviceTagStore"/> asks <see cref="RefusalFor"/> inside its write transaction, next to the
/// device-group guard; <see cref="ChangeRefusalFor"/> refuses a rename or delete only while the plugin runs. A plugin's set
/// is kept while the plugin is installed: a stop, a fault or a turned-off package only closes its views (<see cref="Detach"/>; the set stays with the refusal texts it had), the sets are stored in the server
/// setting <see cref="SettingKey"/> and restored at server start before any tag write (<see cref="LoadAsync"/>), and a set
/// is forgotten only when its plugin is no longer installed (<see cref="LoadAsync"/> with the installed ids, or
/// <see cref="RemoveOwner"/>). Thread-safe; a lookup is a hash lookup per name.
/// </summary>
public sealed partial class ProtectedTagRegistry
{
    /// <summary>
    /// The server setting with the stored sets: JSON object plugin id -> tag name -> { "refusal", "change" } (the texts the
    /// plugin gave for adding or removing the tag and for renaming or deleting it; "" = the host's default).
    /// </summary>
    public const string SettingKey = "Tags.Protected";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock _gate = new();
    private readonly object _writes = new(); // Monitor.Wait needs an object (the in-flight tag writes)
    private readonly SortedSet<long> _activeWrites = [];
    private long _writeSequence;
    private readonly Dictionary<string, Owner> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly ServerSettingsStore? _settings;
    private readonly ILogger _logger;
    private Task _writer = Task.CompletedTask;
    private bool _writing;
    private long _version;
    private long _written;

    /// <summary>A registry in memory only (tests).</summary>
    public ProtectedTagRegistry()
        : this(null, null)
    {
    }

    /// <summary>A registry that stores its sets in <paramref name="settings"/> (the server).</summary>
    public ProtectedTagRegistry(ServerSettingsStore? settings, ILogger<ProtectedTagRegistry>? logger)
    {
        _settings = settings;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>The <see cref="IProtectedTags"/> of one core plugin (at its start). A kept set stays until the plugin sets a new one.</summary>
    public IProtectedTags For(string ownerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        var view = new OwnerTags(this, ownerId);
        lock (_gate)
        {
            if (_owners.TryGetValue(ownerId, out var owner))
            {
                owner.Views.Add(view);
            }
            else
            {
                _owners[ownerId] = new Owner([view], null);
            }
        }

        return view;
    }

    /// <summary>
    /// The plugin stopped, faulted or was turned off: its views set nothing any more, its set stays (with the refusal texts
    /// it had, the plugin's code is no longer asked).
    /// </summary>
    public void Detach(string ownerId)
    {
        lock (_gate)
        {
            if (_owners.TryGetValue(ownerId, out var owner))
            {
                foreach (var view in owner.Views)
                {
                    view.Closed = true;
                }

                _owners[ownerId] = new Owner([], owner.Set is { } set ? set with { Live = null, LiveChange = null } : null);
            }
        }
    }

    /// <summary>Forgets the names of <paramref name="ownerId"/> (the plugin is no longer installed); its views set nothing any more.</summary>
    public void RemoveOwner(string ownerId)
    {
        lock (_gate)
        {
            if (!_owners.Remove(ownerId, out var owner))
            {
                return;
            }

            foreach (var view in owner.Views)
            {
                view.Closed = true;
            }
        }

        SchedulePersist();
    }

    /// <summary>
    /// Server start, after the plugins were loaded and before any tag write: restores the stored sets of the plugins that
    /// have no set yet. <paramref name="installed"/> names the installed core plugins (on or off): a stored set of any other
    /// plugin is forgotten (the plugin was removed). Null keeps every stored set (a plugin failed to load, so it may still be
    /// installed). Returns the ids of the forgotten plugins.
    /// </summary>
    public async Task<IReadOnlyList<string>> LoadAsync(IReadOnlySet<string>? installed, CancellationToken ct)
    {
        if (_settings is null)
        {
            return [];
        }

        var json = await _settings.GetJsonAsync(SettingKey, ct).ConfigureAwait(false);
        Dictionary<string, Dictionary<string, TagTexts>>? stored = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                stored = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, TagTexts>>>(json, Json);
            }
            catch (JsonException ex)
            {
                LogUnreadable(ex);
            }
        }

        var forgotten = new List<string>();
        lock (_gate)
        {
            foreach (var (ownerId, texts) in stored ?? [])
            {
                if (string.IsNullOrWhiteSpace(ownerId) || texts is null)
                {
                    continue;
                }

                if (installed is not null && !installed.Contains(ownerId))
                {
                    forgotten.Add(ownerId);
                    continue;
                }

                var set = new ProtectedSet(Clean(texts), null, null);
                if (!_owners.TryGetValue(ownerId, out var owner))
                {
                    _owners[ownerId] = new Owner([], set);
                }
                else if (owner.Set is null)
                {
                    _owners[ownerId] = owner with { Set = set };
                }
            }
        }

        foreach (var ownerId in forgotten)
        {
            LogForgotten(ownerId);
        }

        if (forgotten.Count > 0)
        {
            SchedulePersist();
            await FlushAsync().ConfigureAwait(false);
        }

        return forgotten;
    }

    /// <summary>Waits until the latest sets are stored (server stop, tests). A failed write is logged and tried once more here.</summary>
    public async Task FlushAsync()
    {
        Task writer;
        lock (_gate)
        {
            writer = _writer;
        }

        await writer.ConfigureAwait(false);
        bool again;
        lock (_gate)
        {
            again = _written != _version;
        }

        if (again)
        {
            SchedulePersist();
            lock (_gate)
            {
                writer = _writer;
            }

            await writer.ConfigureAwait(false);
        }
    }

    /// <summary>Every protected name now (case-insensitive).</summary>
    public IReadOnlySet<string> Names
    {
        get
        {
            lock (_gate)
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var owner in _owners.Values)
                {
                    if (owner.Set is { } set)
                    {
                        names.UnionWith(set.Texts.Keys);
                    }
                }

                return names;
            }
        }
    }

    /// <summary>
    /// The refusal for the first of <paramref name="names"/> a plugin protects, null when none is protected. A running
    /// plugin's text gets the name as the user wrote it; a stopped plugin's set answers with the text it had. A text that
    /// fails or is empty becomes <see cref="DefaultRefusal"/> (the refusal never depends on the plugin's code).
    /// </summary>
    public string? RefusalFor(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        List<ProtectedSet> sets;
        lock (_gate)
        {
            sets = [.. _owners.Values.Select(o => o.Set).OfType<ProtectedSet>().Where(s => s.Texts.Count > 0)];
        }

        if (sets.Count == 0)
        {
            return null;
        }

        foreach (var name in names)
        {
            var trimmed = name.Trim();
            foreach (var set in sets)
            {
                if (set.Texts.TryGetValue(trimmed, out var kept))
                {
                    var text = set.Live is { } live ? TextOf(live, trimmed) : kept.Refusal;
                    return string.IsNullOrWhiteSpace(text) ? DefaultRefusal(trimmed) : text;
                }
            }
        }

        return null;
    }

    /// <summary>"Only users with all devices can add or remove the tag X. Nothing was changed."</summary>
    public static string DefaultRefusal(string name) => $"Only users with all devices can add or remove the tag {name}. Nothing was changed.";

    /// <summary>
    /// The refusal of a rename or delete of the tag <paramref name="name"/> (for every user: the settings of a running plugin
    /// name the tag), null when no running plugin protects it. Only a running plugin's set refuses (its live set, decision
    /// 2026-10-24, final review): while the plugin is stopped, off or faulted (also a lapsed PRO license, which turns a PRO
    /// package off) nobody could change its settings, so the tag is renamed or deleted freely; the plugin's rules keep tags by
    /// name, so a renamed or deleted tag matches nothing (fail closed) and a tag made again under the name is still protected.
    /// A text that fails or is empty becomes <see cref="DefaultChangeRefusal"/>.
    /// </summary>
    public string? ChangeRefusalFor(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = name.Trim();
        List<ProtectedSet> sets;
        lock (_gate)
        {
            sets = [.. _owners.Values.Select(o => o.Set).OfType<ProtectedSet>().Where(s => s.Live is not null)];
        }

        foreach (var set in sets)
        {
            if (set.Texts.TryGetValue(trimmed, out var kept))
            {
                var text = set.LiveChange is { } live ? TextOf(live, trimmed) : kept.Change;
                return string.IsNullOrWhiteSpace(text) ? DefaultChangeRefusal(trimmed) : text;
            }
        }

        return null;
    }

    /// <summary>"A plugin's settings use the tag X. Change them first. Nothing was changed."</summary>
    public static string DefaultChangeRefusal(string name) => $"A plugin's settings use the tag {name}. Change them first. Nothing was changed.";

    private void Set(OwnerTags view, IReadOnlyCollection<string> names, Func<string, string> refusal, Func<string, string>? changeRefusal)
    {
        // The texts are asked once here (outside the lock) and kept for the time the plugin does not run.
        var texts = new Dictionary<string, TagTexts>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()))
        {
            if (!texts.ContainsKey(name))
            {
                texts[name] = new TagTexts(TextOf(refusal, name) ?? "", changeRefusal is null ? "" : TextOf(changeRefusal, name) ?? "");
            }
        }

        var set = new ProtectedSet(texts, refusal, changeRefusal);
        bool changed;
        lock (_gate)
        {
            if (view.Closed || !_owners.TryGetValue(view.OwnerId, out var owner))
            {
                return;
            }

            changed = owner.Set is not { } before || !SameTexts(before.Texts, texts);
            _owners[view.OwnerId] = owner with { Set = set };
        }

        if (changed)
        {
            SchedulePersist();
        }

        WaitForEarlierTagWrites();
    }

    /// <summary>
    /// <see cref="DeviceTagStore"/>: a group user's tag write starts (before its check of the protected names) and ends when
    /// the guard is disposed (after its commit). <see cref="IProtectedTags.Protect"/> returns only after every tag write
    /// that started before the new set ended, so no write checked against the older names commits after a new set is in
    /// force (a VLAN rule saved meanwhile never takes a tag a group user added).
    /// </summary>
    public IDisposable BeginTagWrite()
    {
        lock (_writes)
        {
            var id = ++_writeSequence;
            _activeWrites.Add(id);
            return new TagWrite(this, id);
        }
    }

    /// <summary>Tests: runs when a <see cref="IProtectedTags.Protect"/> call starts waiting for earlier tag writes.</summary>
    internal Action? WaitingForTagWrites { get; set; }

    private void EndTagWrite(long id)
    {
        lock (_writes)
        {
            if (_activeWrites.Remove(id))
            {
                Monitor.PulseAll(_writes);
            }
        }
    }

    private void WaitForEarlierTagWrites()
    {
        lock (_writes)
        {
            var upTo = _writeSequence;
            var signalled = false;
            while (_activeWrites.Count > 0 && _activeWrites.Min <= upTo)
            {
                if (!signalled)
                {
                    signalled = true;
                    WaitingForTagWrites?.Invoke();
                }

                Monitor.Wait(_writes); // a tag write always ends (its guard is disposed in a finally)
            }
        }
    }

    private sealed class TagWrite(ProtectedTagRegistry registry, long id) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                registry.EndTagWrite(id);
            }
        }
    }

    private static string? TextOf(Func<string, string> refusal, string name)
    {
        try
        {
            return refusal(name);
        }
#pragma warning disable CA1031 // A failing plugin text still refuses, with the host's own text.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    private static bool SameTexts(Dictionary<string, TagTexts> a, Dictionary<string, TagTexts> b) =>
        a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out var text) && text == p.Value);

    private static Dictionary<string, TagTexts> Clean(Dictionary<string, TagTexts> texts)
    {
        var clean = new Dictionary<string, TagTexts>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, text) in texts)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                clean[name.Trim()] = new TagTexts(text?.Refusal ?? "", text?.Change ?? "");
            }
        }

        return clean;
    }

    /// <summary>Stores the latest sets in the background, one write at a time; a burst of changes ends in one write of the last state.</summary>
    private void SchedulePersist()
    {
        if (_settings is null)
        {
            return;
        }

        lock (_gate)
        {
            _version++;
            if (_writing)
            {
                return;
            }

            _writing = true;
            using (ExecutionContext.SuppressFlow()) // never a user's call context in the background write
            {
                _writer = Task.Run(WriteLoopAsync);
            }
        }
    }

    private async Task WriteLoopAsync()
    {
        while (true)
        {
            long version;
            string? json;
            lock (_gate)
            {
                if (_written == _version)
                {
                    _writing = false;
                    return;
                }

                version = _version;
                var stored = _owners
                    .Where(o => o.Value.Set is { Texts.Count: > 0 })
                    .ToDictionary(o => o.Key, o => o.Value.Set!.Texts.ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal), StringComparer.Ordinal);
                json = stored.Count == 0 ? null : JsonSerializer.Serialize(stored, Json);
            }

            try
            {
                await _settings!.SetJsonAsync(SettingKey, json, CancellationToken.None).ConfigureAwait(false);
                lock (_gate)
                {
                    _written = version;
                }
            }
#pragma warning disable CA1031 // Logged; the next change or FlushAsync writes again. The sets in memory still apply.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogWriteFailed(ex);
                lock (_gate)
                {
                    _writing = false;
                }

                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The stored protected tags could not be read; none restored")]
    private partial void LogUnreadable(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Protected tags of plugin {PluginId} forgotten: the plugin is no longer installed")]
    private partial void LogForgotten(string pluginId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The protected tags could not be stored")]
    private partial void LogWriteFailed(Exception ex);

    /// <summary>The texts a plugin gave for one tag ("" = the host's default).</summary>
    /// <param name="Refusal">Adding or removing it (a user with a device group).</param>
    /// <param name="Change">Renaming or deleting it (every user).</param>
    private sealed record TagTexts(string Refusal, string Change);

    /// <param name="Texts">Name -> the texts it had.</param>
    /// <param name="Live">The running plugin's add-or-remove text, null while the plugin does not run.</param>
    /// <param name="LiveChange">The running plugin's rename-or-delete text, null while it does not run or gave none.</param>
    private sealed record ProtectedSet(Dictionary<string, TagTexts> Texts, Func<string, string>? Live, Func<string, string>? LiveChange);

    private sealed record Owner(List<OwnerTags> Views, ProtectedSet? Set);

    private sealed class OwnerTags(ProtectedTagRegistry registry, string ownerId) : IProtectedTags
    {
        public string OwnerId => ownerId;

        /// <summary>The plugin stopped (set under the registry's lock).</summary>
        public bool Closed { get; set; }

        public void Protect(IReadOnlyCollection<string> names, Func<string, string> refusal)
        {
            ArgumentNullException.ThrowIfNull(names);
            ArgumentNullException.ThrowIfNull(refusal);
            registry.Set(this, names, refusal, null);
        }

        public void Protect(IReadOnlyCollection<string> names, Func<string, string> refusal, Func<string, string> changeRefusal)
        {
            ArgumentNullException.ThrowIfNull(names);
            ArgumentNullException.ThrowIfNull(refusal);
            ArgumentNullException.ThrowIfNull(changeRefusal);
            registry.Set(this, names, refusal, changeRefusal);
        }
    }
}
