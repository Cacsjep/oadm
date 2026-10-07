using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Sdk.Devices;

namespace Oadm.Plugins.VapixCommander.Client;

/// <summary>
/// A node of the library tree: a group ("Built-in", "Saved"), a category ("Video · 12") or a command (with its write /
/// dangerous badges).
/// </summary>
public sealed partial class LibraryNodeViewModel : ObservableObject
{
    private LibraryNodeViewModel(string title, CommandListItem? item, IEnumerable<LibraryNodeViewModel> children)
    {
        Title = title;
        Item = item;
        Children = [.. children];
    }

    public string Title { get; }

    public CommandListItem? Item { get; }

    public ObservableCollection<LibraryNodeViewModel> Children { get; }

    public bool IsCommand => Item is not null;

    public bool IsGroup => Item is null;

    public bool IsWrite => Item?.Command is { Writes: true, Dangerous: false };

    public bool IsDangerous => Item?.Command.Dangerous == true;

    public bool IsSaved => Item?.Source == CommandSources.Saved;

    public string? Tooltip => Item?.Command.Description;

    /// <summary>Commands below this node (for group titles).</summary>
    public int CommandCount => IsCommand ? 1 : Children.Sum(c => c.CommandCount);

    public string CountText => CommandCount.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public static LibraryNodeViewModel Command(CommandListItem item) => new(item.Command.Name, item, []);

    public static LibraryNodeViewModel Group(string title, IEnumerable<LibraryNodeViewModel> children, bool expanded) =>
        new(title, null, children) { IsExpanded = expanded };

    /// <summary>
    /// Builds the tree: "Built-in" and "Saved", each grouped by category in library order; only commands matching
    /// <paramref name="search"/> (name, id, description, category, path). With a search everything is expanded.
    /// </summary>
    public static List<LibraryNodeViewModel> BuildTree(IReadOnlyList<CommandListItem> library, IReadOnlyList<CommandListItem> saved, string? search)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(saved);
        var filter = search?.Trim();
        var searching = !string.IsNullOrEmpty(filter);
        bool Matches(CommandListItem item) => !searching
            || item.Command.Name.Contains(filter!, StringComparison.OrdinalIgnoreCase)
            || item.Command.Id.Contains(filter!, StringComparison.OrdinalIgnoreCase)
            || item.Command.Category.Contains(filter!, StringComparison.OrdinalIgnoreCase)
            || (item.Command.Description?.Contains(filter!, StringComparison.OrdinalIgnoreCase) ?? false)
            || item.Command.Request.Path.Contains(filter!, StringComparison.OrdinalIgnoreCase);

        List<LibraryNodeViewModel> Categories(IEnumerable<CommandListItem> items) =>
        [
            .. items.Where(Matches)
                .GroupBy(i => i.Command.Category)
                .OrderBy(g => CategoryOrder(g.Key))
                .Select(g => Group(g.Key, g.OrderBy(i => i.Command.Name, StringComparer.OrdinalIgnoreCase).Select(Command), searching)),
        ];

        var tree = new List<LibraryNodeViewModel>();
        var builtIn = Categories(library);
        if (builtIn.Count > 0)
        {
            tree.Add(Group("Built-in", builtIn, true));
        }

        var own = Categories(saved);
        if (own.Count > 0 || (!searching && saved.Count == 0))
        {
            tree.Add(Group("Saved", own, true));
        }

        return tree;
    }

    private static int CategoryOrder(string category)
    {
        for (var i = 0; i < CommandCategories.All.Count; i++)
        {
            if (CommandCategories.All[i] == category)
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}

/// <summary>Compatibility of one rollout command on one target device (status chip).</summary>
public sealed record CompatibilityChip(string Command, CompatibilityState State, string Text)
{
    public bool IsOk => State == CompatibilityState.Compatible;

    public bool IsWarning => State is CompatibilityState.Unknown;

    public bool IsError => State is CompatibilityState.MissingApi or CompatibilityState.VersionTooOld or CompatibilityState.NoVideo;

    public string Line => Command + ": " + Text;
}

/// <summary>A managed device in the target list (checkbox) with the compatibility of every rollout command.</summary>
public sealed partial class TargetDeviceViewModel : ObservableObject
{
    public TargetDeviceViewModel(IDeviceInfo device)
    {
        Device = device;
    }

    public IDeviceInfo Device { get; private set; }

    public Guid Id => Device.Id;

    public string Address => Device.Address;

    public string Title => string.IsNullOrEmpty(Device.Model) ? Device.Address : Device.Address + " · " + Device.Model;

    public string? Subtitle => Device.FirmwareVersion is null ? Device.Serial : $"{Device.Serial} · AXIS OS {Device.FirmwareVersion}";

    public bool IsReachable => Device.Status is DeviceStatus.Ok or DeviceStatus.Unknown;

    public string? StatusText => Device.Status switch
    {
        DeviceStatus.Ok or DeviceStatus.Unknown => null,
        DeviceStatus.Unreachable => "Unreachable",
        DeviceStatus.CredentialsRequired => "Credentials required",
        DeviceStatus.PasswordNotSet => "Password not set",
        DeviceStatus.CertificateChanged => "Certificate changed",
        _ => Device.Status.ToString(),
    };

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public ObservableCollection<CompatibilityChip> Compatibility { get; } = [];

    public bool AllCompatible => Compatibility.Count > 0 && Compatibility.All(c => c.IsOk);

    public void Update(IDeviceInfo device)
    {
        Device = device;
        OnPropertyChanged(string.Empty);
    }

    public void SetCompatibility(IEnumerable<CompatibilityChip> chips)
    {
        Compatibility.Clear();
        foreach (var chip in chips)
        {
            Compatibility.Add(chip);
        }

        OnPropertyChanged(nameof(AllCompatible));
    }

    public bool Matches(string? search) =>
        string.IsNullOrWhiteSpace(search)
        || Device.Address.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
        || (Device.Model?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ?? false)
        || Device.Serial.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
        || (Device.HostName?.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>The Postman-like result of "Try on one device": status, duration, interpreted result, headers and body.</summary>
public sealed class TryResultViewModel
{
    public TryResultViewModel(string commandName, string deviceAddress, TryOutcome? outcome, string? error)
    {
        CommandName = commandName;
        DeviceAddress = deviceAddress;
        Outcome = outcome;
        Error = error;
    }

    public string CommandName { get; }

    public string DeviceAddress { get; }

    public TryOutcome? Outcome { get; }

    /// <summary>Nothing was sent (validation, compatibility, device state).</summary>
    public string? Error { get; }

    public bool Success => Outcome?.Success == true;

    public bool IsError => !Success;

    public string StatusText => Error is not null ? "Not sent" : Success ? "Success" : "Failed";

    /// <summary>The interpreted result or the error text (device or transport).</summary>
    public string Summary => Error ?? Outcome?.Summary ?? string.Empty;

    /// <summary>"HTTP 200 OK · 84 ms · 10.0.0.48" or "No response · 15003 ms · 10.0.0.48".</summary>
    public string ExchangeLine
    {
        get
        {
            if (Outcome is null)
            {
                return DeviceAddress;
            }

            var status = Outcome.StatusCode is { } code
                ? string.Create(CultureInfo.InvariantCulture, $"HTTP {code} {Outcome.ReasonPhrase}").TrimEnd()
                : "No response";
            return string.Create(CultureInfo.InvariantCulture, $"{status} · {Outcome.DurationMs:0} ms · {DeviceAddress}");
        }
    }

    public string? RequestLine => Outcome?.RequestLine;

    public bool HasRequestBody => !string.IsNullOrEmpty(Outcome?.RequestBody);

    public IReadOnlyList<NamedValue> Values => Outcome?.Values ?? [];

    public bool HasValues => Values.Count > 0;

    public string HeadersText => Outcome is null ? string.Empty : string.Join(Environment.NewLine, Outcome.Headers.Select(h => $"{h.Name}: {h.Value}"));

    public bool HasHeaders => Outcome?.Headers.Count > 0;

    public string Body => Outcome?.Body is null ? string.Empty : Outcome.Body + (Outcome.BodyTruncated ? Environment.NewLine + "… (cut at 256 KB)" : string.Empty);

    public bool HasBody => !string.IsNullOrEmpty(Outcome?.Body);
}
