using System.Collections.ObjectModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using Oadm.Sdk.Client.Controls;
using Oadm.Sdk.Devices;

namespace Oadm.Plugins.VapixCommander.Client;

/// <summary>
/// A node of the library tree: a group ("Built-in", "Saved"), a category ("Video") or a command. No badges in the tree
/// (no counts, no write / dangerous): the tooltip, the rollout set (Kind) and the run confirmation say it.
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

    /// <summary>"Write · dangerous" (kind of a changing command) and the description.</summary>
    public string? Tooltip => Item is null ? null
        : Item.Command.Writes || Item.Command.Dangerous
            ? (Item.Command.Dangerous ? "Write · dangerous" : "Write") + (string.IsNullOrEmpty(Item.Command.Description) ? string.Empty : Environment.NewLine + Item.Command.Description)
            : Item.Command.Description;

    /// <summary>Commands below this node.</summary>
    public int CommandCount => IsCommand ? 1 : Children.Sum(c => c.CommandCount);

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

    /// <summary>Compatibility of every rollout command, in rollout order (empty without commands).</summary>
    public IReadOnlyList<CompatibilityChip> Compatibility { get; private set; } = [];

    public bool AllCompatible { get; private set; }

    public bool HasCompatibility => Compatibility.Count > 0;

    /// <summary>
    /// The per-device line is shown only when something is not compatible; compatible devices stay
    /// quiet (thousands of green lines are noise). The summary above the list still counts them.
    /// </summary>
    public bool ShowCompatibility => HasCompatibility && !AllCompatible;

    /// <summary>One chip per device: "Compatible", else the first problem ("Missing API x (+1 more)").</summary>
    public string? CompatibilityText { get; private set; }

    /// <summary>Every command with its compatibility, one per line (tooltip of the chip).</summary>
    public string? CompatibilityTooltip => HasCompatibility ? string.Join(Environment.NewLine, Compatibility.Select(c => c.Line)) : null;

    public bool IsCompatibilityError { get; private set; }

    public bool IsCompatibilityWarning => HasCompatibility && !AllCompatible && !IsCompatibilityError;

    public void Update(IDeviceInfo device)
    {
        Device = device;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Sets the compatibility of the rollout commands (computed by the page from the cached API list).</summary>
    public void SetCompatibility(IReadOnlyList<CompatibilityChip> chips)
    {
        ArgumentNullException.ThrowIfNull(chips);
        if (chips.Count == 0 && Compatibility.Count == 0)
        {
            return;
        }

        Compatibility = chips;
        CompatibilityChip? firstProblem = null;
        var problems = 0;
        var errors = false;
        foreach (var chip in chips)
        {
            if (!chip.IsOk)
            {
                firstProblem ??= chip;
                problems++;
                errors |= chip.IsError;
            }
        }

        AllCompatible = chips.Count > 0 && problems == 0;
        IsCompatibilityError = errors;
        CompatibilityText = chips.Count == 0 ? null
            : firstProblem is null ? "Compatible"
            : problems == 1 ? firstProblem.Line
            : string.Create(CultureInfo.InvariantCulture, $"{firstProblem.Line} (+{problems - 1} more)");
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Address, model, serial or host name contains <paramref name="search"/> (trimmed by the caller; empty matches all).</summary>
    public bool Matches(string search) =>
        search.Length == 0
        || Device.Address.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (Device.Model?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
        || Device.Serial.Contains(search, StringComparison.OrdinalIgnoreCase)
        || (Device.HostName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>The Postman-like result of "Try on one device": status, duration, interpreted result, headers and body.</summary>
public sealed partial class TryResultViewModel : ObservableObject
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

    /// <summary>The body as the server prepared it (JSON and XML indented).</summary>
    public string Body => Outcome?.Body ?? string.Empty;

    /// <summary>The body exactly as the device sent it.</summary>
    public string RawBody => Outcome?.RawBody ?? Body;

    public bool HasBody => !string.IsNullOrEmpty(Outcome?.Body);

    public string? ContentType => Outcome?.ContentType;

    /// <summary>Highlighting of the body, from the response content type and else from the text (param.cgi key=value).</summary>
    public CodeLanguage BodyLanguage => HasBody ? CodeText.Detect(RawBody, ContentType) : CodeLanguage.Plain;

    /// <summary>JSON and XML can be shown pretty-printed (default) or exactly as sent.</summary>
    public bool CanShowRaw => BodyLanguage is CodeLanguage.Json or CodeLanguage.Xml;

    /// <summary>True (default): pretty-printed; false: the exact device text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRaw), nameof(DisplayBody))]
    public partial bool IsPretty { get; set; } = true;

    public bool IsRaw => !IsPretty;

    public string DisplayBody => IsPretty ? Body : RawBody;

    public bool IsBodyTruncated => Outcome?.BodyTruncated == true;

    [RelayCommand]
    private void ShowPretty() => IsPretty = true;

    [RelayCommand]
    private void ShowRaw() => IsPretty = false;
}
