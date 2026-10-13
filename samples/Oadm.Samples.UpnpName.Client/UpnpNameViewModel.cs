using System.Text.Json;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Validation;

namespace Oadm.Samples.UpnpName.Client;

public sealed partial class UpnpNameViewModel : ValidatingViewModel
{
    public UpnpNameViewModel(int deviceCount)
    {
        Intro = deviceCount == 1 ? "The name the device shows in the network list." : $"The name {deviceCount} devices show in the network list.";
        Validation.Rule(nameof(Name), () => UpnpName.Problem(Name));
        Validation.Validate();
    }

    public string Intro { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    /// <summary>The window closes with true (OK) or false (Cancel).</summary>
    public event Action<bool>? CloseRequested;

    public async Task LoadAsync(Func<CancellationToken, Task<string?>> query, CancellationToken ct)
    {
        try
        {
            var json = await query(ct);
            if (Name.Length == 0 && json is not null && JsonSerializer.Deserialize<UpnpNamePayload>(json, UpnpName.Json) is { } current)
            {
                Name = current.Name;
                Validation.Reset(nameof(Name)); // a prefilled value shows no error yet
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No prefill: the user types the name.
        }
    }

    [RelayCommand(CanExecute = nameof(IsFormValid))]
    private void Apply() => CloseRequested?.Invoke(true);

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);

    protected override void OnValidationChanged() => ApplyCommand.NotifyCanExecuteChanged();
}
