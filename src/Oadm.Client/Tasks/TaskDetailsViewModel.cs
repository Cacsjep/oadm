using Oadm.Client.Devices;
using Oadm.Contracts.V1;

namespace Oadm.Client.Tasks;

public sealed record TaskDeviceRow(string Device, string Model, string StateText, PillKind StateKind, string Message, int Progress)
{
    public bool IsStateOk => StateKind == PillKind.Ok;
    public bool IsStateError => StateKind == PillKind.Error;
    public bool IsStateAccent => StateKind == PillKind.Accent;
    public bool IsStateNeutral => StateKind == PillKind.Neutral;
}

/// <summary>Per-device results of one task, shown by the Details button.</summary>
public sealed class TaskDetailsViewModel
{
    public TaskDetailsViewModel(TaskRowViewModel task, DeviceStore devices)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(devices);
        Task = task;
        Rows = task.DeviceResults.Select(r =>
        {
            DeviceRowViewModel? device = devices.Find(r.DeviceId);
            string label = device is null ? r.DeviceId : $"{device.DisplayAddress} ({device.Serial})";
            return new TaskDeviceRow(label, device?.Model ?? "", TaskRowViewModel.ToText(r.State), TaskRowViewModel.ToKind(r.State),
                r.Message, Math.Clamp(r.Progress, 0, 100));
        }).ToList();
    }

    public TaskRowViewModel Task { get; }
    public IReadOnlyList<TaskDeviceRow> Rows { get; }
    public string Title => $"{Task.Name} - {Task.StateText}";
}
