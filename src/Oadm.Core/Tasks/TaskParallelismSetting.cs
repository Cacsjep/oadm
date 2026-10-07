using System.Text.Json;

using Oadm.Core.Settings;

namespace Oadm.Core.Tasks;

/// <summary>
/// Live value of the server setting <c>Tasks.MaxParallelPerPlugin</c> (default 16, 1..256) for the task
/// engine (<see cref="TaskEngineOptions.MaxParallelTasksPerPluginSource"/>). <see cref="LoadAsync"/> reads
/// the stored value at startup; every later write through <see cref="ServerSettingsStore"/> updates it and
/// raises <see cref="Changed"/> (the server host then calls <see cref="TaskEngine.RescheduleQueued"/>).
/// </summary>
public sealed class TaskParallelismSetting : IDisposable
{
    private readonly ServerSettingsStore _store;
    private int _value = ServerSettings.DefaultMaxParallelTasksPerPlugin;

    public TaskParallelismSetting(ServerSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _store.Changed += OnSettingChanged;
    }

    /// <summary>The current per-plugin limit.</summary>
    public int Current => Volatile.Read(ref _value);

    /// <summary>Raised after <see cref="Current"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Reads the stored value (or the default).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        var settings = await _store.GetServerSettingsAsync(ct).ConfigureAwait(false);
        Set(settings.MaxParallelTasksPerPlugin);
    }

    public void Dispose() => _store.Changed -= OnSettingChanged;

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Key != SettingKeys.TasksMaxParallelPerPlugin)
        {
            return;
        }

        var value = ServerSettings.DefaultMaxParallelTasksPerPlugin;
        if (e.ValueJson is not null)
        {
            try
            {
                value = JsonSerializer.Deserialize<int>(e.ValueJson);
            }
            catch (JsonException)
            {
                return;
            }
        }

        Set(value);
    }

    private void Set(int value)
    {
        value = Math.Clamp(value, ServerSettings.MinMaxParallelTasksPerPlugin, ServerSettings.MaxMaxParallelTasksPerPlugin);
        if (Interlocked.Exchange(ref _value, value) != value)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
