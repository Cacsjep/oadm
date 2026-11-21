using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Validation;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.Overlays.Client;

/// <summary>
/// The "Text overlays" dialog: a list of at most <see cref="OverlayRules.MaxOverlays"/> text overlays, read from a camera or
/// typed; the menu entry fixes what happens on each camera (Add, Replace or Remove matching overlays). Reads the limits of the first
/// compatible selected camera once (one query, whatever the selection), and a camera's overlays on Read from a camera.
/// Every error shows below its field; Apply stays disabled while any exists.
/// </summary>
public sealed partial class OverlaysDialogViewModel : ValidatingViewModel
{
    /// <summary>Validation key of the whole list (its error shows directly below the list).</summary>
    public const string OverlaysField = "Overlays";

    public const string EmptyList = "Add an overlay, or read them from a camera.";

    private readonly IReadOnlyList<IDeviceInfo> _devices;
    private readonly IReadOnlyList<IDeviceInfo> _allDevices;
    private readonly Func<Guid, string, CancellationToken, Task<string?>> _query;
    private readonly Func<IReadOnlyList<IDeviceInfo>, Task<IDeviceInfo?>> _pickCamera;
    private readonly int _compatible;
    private OverlayLimits _limits = OverlayLimits.Default;
    private CancellationToken _closing;

    /// <param name="mode">What the menu entry does: Add, Replace or Remove matching (Remove all has no dialog).</param>
    /// <param name="devices">The selected devices (each gets a task).</param>
    /// <param name="allDevices">The devices the user may read from (Read from a camera).</param>
    /// <param name="query">The plugin's query: device id, method → JSON.</param>
    /// <param name="pickCamera">The one-device picker; null = cancelled.</param>
    public OverlaysDialogViewModel(
        OverlaysMode mode,
        IReadOnlyList<IDeviceInfo> devices,
        IReadOnlyList<IDeviceInfo> allDevices,
        Func<Guid, string, CancellationToken, Task<string?>> query,
        Func<IReadOnlyList<IDeviceInfo>, Task<IDeviceInfo?>> pickCamera)
    {
        if (mode is not (OverlaysMode.Add or OverlaysMode.Replace or OverlaysMode.RemoveMatching))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Remove all has no dialog.");
        }

        Mode = mode;
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _allDevices = allDevices ?? throw new ArgumentNullException(nameof(allDevices));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _pickCamera = pickCamera ?? throw new ArgumentNullException(nameof(pickCamera));
        var compatible = 0;
        foreach (var device in devices)
        {
            if (OverlaysTaskPlugin.CanRunOn(device))
            {
                compatible++;
            }
        }

        _compatible = compatible;
        Rows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRows));
            OnPropertyChanged(nameof(ShowEmptyLine));
            AddOverlayCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(AddOverlayTip));
            Validation.Touch(OverlaysField);
            Update();
        };
        Validation.Rule(OverlaysField, () => Rows.Count == 0 ? OverlayRules.AddOne : null);
        AddRow(null);
        Validation.Reset();
        Update();
    }

    /// <summary>Raised with the payload JSON (Apply) or null (Cancel).</summary>
    public event EventHandler<string?>? CloseRequested;

    public ObservableCollection<OverlayRowViewModel> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public bool ShowEmptyLine => Rows.Count == 0;

    public int CompatibleCount => _compatible;

    /// <summary>"7 of 250 cameras are not supported."; null when every selected device is.</summary>
    public string? UnsupportedText => OverlayTexts.Unsupported(_devices.Count, _compatible);

    /// <summary>What the menu entry does on each camera.</summary>
    public OverlaysMode Mode { get; }

    /// <summary>The window title: the menu entry's name.</summary>
    public string Title => Mode switch
    {
        OverlaysMode.Replace => "Replace overlays",
        OverlaysMode.RemoveMatching => "Remove matching overlays",
        _ => "Add overlays",
    };

    /// <summary>Remove matching: only the Text column, the red main button.</summary>
    public bool IsRemove => Mode == OverlaysMode.RemoveMatching;

    /// <summary>Replace: its warning above the table.</summary>
    public bool IsReplace => Mode == OverlaysMode.Replace;

    public static string ReplaceNote => OverlaysTaskPlugin.ReplaceNote;

    /// <summary>The main button names the action.</summary>
    public string ApplyText => Mode switch
    {
        OverlaysMode.Replace => "Replace overlays",
        OverlaysMode.RemoveMatching => "Remove overlays",
        _ => "Add overlays",
    };

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial string LoadingText { get; private set; } = string.Empty;

    /// <summary>What the last read found ("Read 2 overlays from ..."); null before one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadStatus))]
    public partial string? ReadStatus { get; private set; }

    [ObservableProperty]
    public partial bool ReadFailed { get; private set; }

    public bool HasReadStatus => !string.IsNullOrEmpty(ReadStatus);

    /// <summary>The list's error, directly below the list.</summary>
    public string? OverlaysError => ErrorOf(OverlaysField);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool CanApply { get; private set; }

    /// <summary>Why Apply is disabled (tooltip).</summary>
    [ObservableProperty]
    public partial string? ApplyBlockedReason { get; private set; }

    /// <summary>The limits used for the checks (the camera read last, else the API's).</summary>
    public OverlayLimits Limits => _limits;

    /// <summary>The Size header's tooltip ("16 to 200; empty: the camera's default").</summary>
    public string SizeTip => _limits.SizeTip;

    /// <summary>The button below the list: Remove matching lists texts to find, the other dialogs overlays to write.</summary>
    public string AddRowText => IsRemove ? "Add text" : "Add overlay";

    /// <summary>Why Add overlay is disabled (its tooltip); null while it can add.</summary>
    public string? AddOverlayTip => CanAddOverlay() ? null : OverlayRules.TooMany;

    /// <summary>Reads the limits of the first compatible selected camera. Never throws: the dialog works without them.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        _closing = ct;
        var first = _devices.FirstOrDefault(OverlaysTaskPlugin.CanRunOn);
        if (first is null)
        {
            return;
        }

        try
        {
            var result = OverlaysJson.ParseQueryResult(await _query(first.Id, OverlaysJson.ListOverlaysMethod, ct).ConfigureAwait(true));
            if (result is { Supported: true, Capabilities: { } capabilities })
            {
                SetLimits(OverlayLimits.Of(capabilities));
            }
        }
        catch (OperationCanceledException)
        {
            // the dialog closed
        }
#pragma warning disable CA1031 // Without the camera's limits the API's apply; each camera checks its own before the first write.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddOverlay))]
    private void AddOverlay()
    {
        if (CanAddOverlay())
        {
            AddRow(null);
        }
    }

    private bool CanAddOverlay() => Rows.Count < OverlayRules.MaxOverlays;

    /// <summary>Asks for one camera and puts its text overlays of view area 1 into the list.</summary>
    [RelayCommand]
    private async Task ReadFromCameraAsync()
    {
        var source = _allDevices.Count > 0 ? _allDevices : _devices;
        if (await _pickCamera(source).ConfigureAwait(true) is not { } camera)
        {
            return;
        }

        IsLoading = true;
        LoadingText = "Reading the overlays";
        try
        {
            var result = OverlaysJson.ParseQueryResult(await _query(camera.Id, OverlaysJson.ListOverlaysMethod, _closing).ConfigureAwait(true));
            if (result is null || !result.Supported)
            {
                SetReadStatus(result?.Message ?? "The camera sent no overlays.", failed: true);
                return;
            }

            if (result.Capabilities is { } capabilities)
            {
                SetLimits(OverlayLimits.Of(capabilities));
            }

            Apply(result.Overlays);
            var name = Describe(camera);
            var count = result.Overlays.Count;
            SetReadStatus(count switch
            {
                0 => $"{name} has no text overlays in view area 1.",
                1 => $"Read 1 overlay from {name}.",
                _ when count > OverlayRules.MaxOverlays => string.Create(CultureInfo.InvariantCulture, $"Read the first {OverlayRules.MaxOverlays} of {count} overlays from {name}."),
                _ => string.Create(CultureInfo.InvariantCulture, $"Read {count} overlays from {name}."),
            }, failed: false);
        }
        catch (OperationCanceledException)
        {
            SetReadStatus(null, failed: false);
        }
#pragma warning disable CA1031 // A failed read is shown in the dialog, which stays usable.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            SetReadStatus("The overlays could not be read: " + ex.Message, failed: true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Replaces the list with these overlays (the first <see cref="OverlayRules.MaxOverlays"/>).</summary>
    public void Apply(IReadOnlyList<TextOverlay> overlays)
    {
        ArgumentNullException.ThrowIfNull(overlays);
        foreach (var row in Rows)
        {
            row.Changed -= OnRowChanged;
        }

        Rows.Clear();
        foreach (var overlay in overlays.Take(OverlayRules.MaxOverlays))
        {
            AddRow(overlay);
        }
    }

    /// <summary>The task payload for the current input, or null while it is invalid.</summary>
    public string? BuildPayload()
    {
        var payload = CreatePayload();
        return Validation.IsValid && Rows.All(r => r.IsValid) && OverlayRules.Check(payload).Count == 0
            ? OverlaysJson.Serialize(new OverlayListPayload { Overlays = payload.Overlays })
            : null;
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        Validation.ShowAll();
        foreach (var row in Rows)
        {
            row.ShowErrors();
        }

        if (BuildPayload() is { } payload)
        {
            CloseRequested?.Invoke(this, payload);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, null);

    protected override void OnValidationChanged()
    {
        OnPropertyChanged(nameof(OverlaysError));
        Update();
    }

    private OverlaysPayload CreatePayload() => new()
    {
        Mode = Mode,
        Overlays = [.. Rows.Select(r => r.ToOverlay())],
    };

    private void AddRow(TextOverlay? overlay)
    {
        var row = new OverlayRowViewModel(() => _limits, r =>
        {
            r.Changed -= OnRowChanged;
            Rows.Remove(r);
        }, overlay)
        {
            ShowStyle = Mode != OverlaysMode.RemoveMatching,
        };
        row.Changed += OnRowChanged;
        Rows.Add(row);
    }

    private void OnRowChanged(object? sender, EventArgs e) => Update();

    private void SetLimits(OverlayLimits limits)
    {
        _limits = limits;
        foreach (var row in Rows)
        {
            row.Revalidate();
        }

        OnPropertyChanged(nameof(Limits));
        OnPropertyChanged(nameof(SizeTip));
        Update();
    }

    private void SetReadStatus(string? text, bool failed)
    {
        ReadStatus = text;
        ReadFailed = failed;
    }

    private void Update()
    {
        Validation.Validate();
        var rowError = Rows.Select(r => r.IsValid ? null : r.FormError).FirstOrDefault(e => e is not null);
        var reason = _compatible == 0 ? "None of the selected devices supports text overlays."
            : Validation.FirstError ?? rowError
            ?? (OverlayRules.Check(CreatePayload()) is [var first, ..] ? first.Message : null);
        ApplyBlockedReason = reason;
        CanApply = reason is null;
    }

    private static string Describe(IDeviceInfo device) =>
        string.IsNullOrEmpty(device.Model) ? device.Address : $"{device.Model} ({device.Address})";
}
