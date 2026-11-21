using System.Text.Json.Nodes;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.DeviceConfig;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>A row of the file preview: an area's heading with its count.</summary>
public sealed record PreviewHeading(string Text);

/// <summary>A row of the file preview: a line about the area (what stays on each camera, or that the file has none).</summary>
public sealed record PreviewNote(string Text);

/// <summary>A row of the file preview: one parameter ("Name = value"), action rule or schedule.</summary>
public sealed record PreviewEntry(string Text);

/// <summary>What one configuration file holds, read once per file: what Import configuration applies of each area.</summary>
public sealed record ConfigFileContent(IReadOnlyList<NameValue> Parameters, int LeftOutParameters, IReadOnlyList<string> ActionRules, IReadOnlyList<string> Schedules)
{
    /// <summary>
    /// The areas of a file of Export configuration as Import configuration applies them: the parameters without the ones
    /// it never writes (<see cref="ConfigIdentity.Clean"/>: identity, read-only, time zone and, as the node never chooses
    /// them, the connection settings), the action rules and the schedules by name. Throws <see cref="ConfigFileException"/>.
    /// </summary>
    public static ConfigFileContent Parse(string json)
    {
        var file = ConfigFile.Parse(json);
        IReadOnlyList<NameValue> parameters = [];
        var leftOut = 0;
        if (file.Data[ConfigSections.Param] is JsonObject param)
        {
            parameters = ConfigParameters.Flatten(ConfigIdentity.Clean(ConfigSections.Param, param));
            leftOut = Math.Max(0, ConfigParameters.Flatten(param).Count - parameters.Count);
        }

        return new ConfigFileContent(parameters, leftOut, Names(file.Data[ConfigSections.ActionRules]?["rules"]), Names(file.Data["event-schedules.v2"]?["schedules"]));
    }

    private static List<string> Names(JsonNode? list) => list is JsonArray array
        ? [.. array.OfType<JsonObject>().Select(item => Text(item["name"]) ?? Text(item["id"]) ?? string.Empty).Where(n => n.Length > 0)]
        : [];

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && text.Trim().Length > 0 ? text.Trim() : null;
}

/// <summary>
/// The file preview below a Configuration node's settings (Settings from: A configuration file): per ticked area its heading
/// with the count and its entries, read only. The file is read from the server once per file (<see cref="INodeFormServices.ReadConfigFileAsync"/>),
/// parsed off the UI thread; ticking an area rebuilds the rows from that read. One list of rows for the virtualized list.
/// </summary>
public sealed partial class ConfigPreviewViewModel : ObservableObject
{
    private readonly ChoiceFieldViewModel _source;
    private readonly FileFieldViewModel _file;
    private readonly BoolFieldViewModel _parameters;
    private readonly BoolFieldViewModel _actionRules;
    private readonly BoolFieldViewModel _schedules;
    private readonly INodeFormServices _services;
    private Guid? _loadedId;
    private ConfigFileContent? _content;
    private int _version;

    public ConfigPreviewViewModel(ChoiceFieldViewModel source, FileFieldViewModel file, BoolFieldViewModel parameters, BoolFieldViewModel actionRules,
        BoolFieldViewModel schedules, INodeFormServices services)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _actionRules = actionRules ?? throw new ArgumentNullException(nameof(actionRules));
        _schedules = schedules ?? throw new ArgumentNullException(nameof(schedules));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>The rows: per ticked area its heading, its note and its entries.</summary>
    public RangeObservableCollection<object> Rows { get; } = [];

    /// <summary>A configuration file is the source and one is chosen.</summary>
    public bool IsShown => _source.Is("file") && _file.FileId is not null;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Why the file could not be read, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public bool HasRows => Rows.Count > 0;

    /// <summary>The read of the file that runs now (tests wait for it).</summary>
    public Task LoadTask { get; private set; } = Task.CompletedTask;

    /// <summary>The node is shown or a field changed: reads a newly chosen file, else rebuilds the rows for the ticked areas.</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsShown));
        if (!IsShown)
        {
            return;
        }

        if (_file.FileId != _loadedId)
        {
            LoadTask = LoadAsync(_file.FileId!.Value);
            return;
        }

        Build();
    }

    private async Task LoadAsync(Guid fileId)
    {
        var version = ++_version;
        _loadedId = fileId;
        _content = null;
        Error = null;
        Rows.ReplaceAll([]);
        OnPropertyChanged(nameof(HasRows));
        IsLoading = true;
        try
        {
            var text = await _services.ReadConfigFileAsync(fileId, CancellationToken.None).ConfigureAwait(true);
            var content = await Task.Run(() => ConfigFileContent.Parse(text)).ConfigureAwait(true);
            if (version != _version)
            {
                return;
            }

            _content = content;
            Build();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _version)
            {
                Error = ex is ConfigFileException ? ex.Message + "." : ServerErrors.Message(ex);
                _loadedId = null; // choosing the node again tries again
            }
        }
        finally
        {
            if (version == _version)
            {
                IsLoading = false;
            }
        }
    }

    private void Build()
    {
        if (_content is not { } content)
        {
            return;
        }

        var rows = new List<object>();
        if (_parameters.IsChecked)
        {
            rows.Add(new PreviewHeading(ProvisioningTexts.AreaHeading(ProvisioningTexts.Parameters, content.Parameters.Count)));
            if (content.Parameters.Count == 0)
            {
                rows.Add(new PreviewNote(ProvisioningTexts.NoParameters));
            }
            else if (content.LeftOutParameters > 0)
            {
                rows.Add(new PreviewNote(ProvisioningTexts.LeftOutParameters(content.LeftOutParameters)));
            }

            rows.AddRange(content.Parameters.Select(p => new PreviewEntry(p.Name + " = " + p.Value)));
        }

        Add(rows, _actionRules.IsChecked, ProvisioningTexts.ActionRules, content.ActionRules, ProvisioningTexts.NoActionRules);
        Add(rows, _schedules.IsChecked, ProvisioningTexts.Schedules, content.Schedules, ProvisioningTexts.NoSchedules);
        Rows.ReplaceAll(rows);
        OnPropertyChanged(nameof(HasRows));
    }

    private static void Add(List<object> rows, bool isChecked, string area, IReadOnlyList<string> names, string none)
    {
        if (!isChecked)
        {
            return;
        }

        rows.Add(new PreviewHeading(ProvisioningTexts.AreaHeading(area, names.Count)));
        if (names.Count == 0)
        {
            rows.Add(new PreviewNote(none));
        }

        rows.AddRange(names.Select(n => new PreviewEntry(n)));
    }
}
