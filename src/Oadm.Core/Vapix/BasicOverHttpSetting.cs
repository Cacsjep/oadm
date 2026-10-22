using System.Text.Json;

using Oadm.Core.Settings;

namespace Oadm.Core.Vapix;

/// <summary>
/// Live value of the server setting <c>Devices.AllowBasicOverHttp</c> (default false) for the device connections
/// (<see cref="VapixClientFactory"/>, the add page's login). <see cref="LoadAsync"/> reads the stored value at startup; every
/// later write through <see cref="ServerSettingsStore"/> updates it and raises <see cref="Changed"/>, so the factory drops
/// its cached clients and the next request uses the new value.
/// </summary>
public sealed class BasicOverHttpSetting : IDisposable
{
    private readonly ServerSettingsStore? _store;
    private int _value;

    public BasicOverHttpSetting(ServerSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _store.Changed += OnSettingChanged;
    }

    /// <summary>A fixed value without a store (tests).</summary>
    public BasicOverHttpSetting(bool value) => _value = value ? 1 : 0;

    /// <summary>Basic login may go over plain HTTP to devices that offer only Basic (Digest stays preferred).</summary>
    public bool Current => Volatile.Read(ref _value) == 1;

    /// <summary>Raised after <see cref="Current"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Reads the stored value (or the default).</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (_store is null)
        {
            return;
        }

        var settings = await _store.GetServerSettingsAsync(ct).ConfigureAwait(false);
        Set(settings.AllowBasicOverHttp);
    }

    /// <summary>Changes the value (tests without a store; the store's writes call it too).</summary>
    public void Set(bool value)
    {
        if (Interlocked.Exchange(ref _value, value ? 1 : 0) != (value ? 1 : 0))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        if (_store is not null)
        {
            _store.Changed -= OnSettingChanged;
        }
    }

    private void OnSettingChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Key != SettingKeys.DevicesAllowBasicOverHttp)
        {
            return;
        }

        var value = false;
        if (e.ValueJson is not null)
        {
            try
            {
                value = JsonSerializer.Deserialize<bool>(e.ValueJson);
            }
            catch (JsonException)
            {
                return;
            }
        }

        Set(value);
    }
}
