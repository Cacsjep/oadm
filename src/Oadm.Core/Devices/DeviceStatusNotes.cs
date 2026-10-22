using System.Collections.Concurrent;

namespace Oadm.Core.Devices;

/// <summary>
/// Why a device has its status, when the status alone does not say it: kept in memory per device, never stored (like the
/// computed Site offline). Today one note: a device that offers only Basic login over plain HTTP while the server setting
/// Devices.AllowBasicOverHttp is off (<see cref="Sdk.Devices.DeviceMessages.BasicOnlyOverHttp"/>). The status check sets and
/// clears it; DeviceService sends it with the device (<c>status_detail</c>). A restart forgets it until the next check.
/// </summary>
public sealed class DeviceStatusNotes
{
    private readonly ConcurrentDictionary<Guid, string> _notes = new();

    /// <summary>The note of a device, null when it has none.</summary>
    public string? Get(Guid deviceId) => _notes.GetValueOrDefault(deviceId);

    /// <summary>Devices that have a note.</summary>
    public IReadOnlyList<Guid> DeviceIds => [.. _notes.Keys];

    /// <summary>Sets (or with null clears) the note of a device. True when it changed.</summary>
    public bool Set(Guid deviceId, string? note)
    {
        if (string.IsNullOrEmpty(note))
        {
            return _notes.TryRemove(deviceId, out _);
        }

        string? before = null;
        _notes.AddOrUpdate(deviceId, note, (_, old) =>
        {
            before = old;
            return note;
        });
        return !string.Equals(before, note, StringComparison.Ordinal);
    }
}
