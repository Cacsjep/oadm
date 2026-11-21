using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Plugins.DeviceConfig;
using Oadm.Sdk.Client;
using Oadm.Sdk.Client.Collections;

namespace Oadm.Plugins.Provisioning.Client;

/// <summary>A row of the file preview: an area's heading with its count.</summary>
public sealed record PreviewHeading(string Text);

/// <summary>A row of the file preview: a line about the area (what stays on each camera, or that the file has none).</summary>
public sealed record PreviewNote(string Text);

/// <summary>A row of the file preview: one parameter ("Name = value"), action rule, schedule or stream profile.</summary>
public sealed record PreviewEntry(string Text);

/// <summary>
/// The file preview below a Configuration node's settings once a file is chosen: per ticked area its heading with the
/// count and its entries, read only, exactly what the node's run applies (<see cref="AdmConfigApply.ForEveryDevice"/>, the
/// payload of Configure devices). The file is read from the server once per file (<see cref="INodeFormServices.ReadConfigFileAsync"/>),
/// parsed off the UI thread; ticking an area rebuilds the rows from that read. A file that is no configuration file, or
/// whose settings Configure devices refuses, is an error below the file field. One list of rows for the virtualized list.
/// </summary>
public sealed partial class ConfigPreviewViewModel : ObservableObject
{
    /// <summary>How the preview shows a password or secret value of the file (like the Configure devices overview).</summary>
    public const string Hidden = "••••••••";

    private readonly FileFieldViewModel _file;
    private readonly BoolFieldViewModel _parameters;
    private readonly BoolFieldViewModel _actionRules;
    private readonly BoolFieldViewModel _streamProfiles;
    private readonly INodeFormServices _services;
    private Guid? _loadedId;
    private AdmConfig? _config;
    private int _version;

    public ConfigPreviewViewModel(FileFieldViewModel file, BoolFieldViewModel parameters, BoolFieldViewModel actionRules,
        BoolFieldViewModel streamProfiles, INodeFormServices services)
    {
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _actionRules = actionRules ?? throw new ArgumentNullException(nameof(actionRules));
        _streamProfiles = streamProfiles ?? throw new ArgumentNullException(nameof(streamProfiles));
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>The rows: per ticked area its heading, its note and its entries.</summary>
    public RangeObservableCollection<object> Rows { get; } = [];

    /// <summary>A configuration file is chosen.</summary>
    public bool IsShown => _file.FileId is not null;

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

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
        _config = null;
        _file.SetContentError(null);
        Rows.ReplaceAll([]);
        OnPropertyChanged(nameof(HasRows));
        IsLoading = true;
        try
        {
            var text = await _services.ReadConfigFileAsync(fileId, CancellationToken.None).ConfigureAwait(true);
            var config = await Task.Run(() => AdmConfigFile.Read(text)).ConfigureAwait(true);
            if (version != _version)
            {
                return;
            }

            _config = config;
            Build();
        }
        catch (FormatException ex)
        {
            if (version == _version)
            {
                _file.SetContentError(ex.Message); // the same file stays wrong: no second read
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _version)
            {
                _file.SetContentError(ServerErrors.Message(ex));
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
        if (_config is not { } config)
        {
            return;
        }

        var areas = new AdmConfigAreas(_parameters.IsChecked, _actionRules.IsChecked, _streamProfiles.IsChecked);
        var plan = AdmConfigApply.ForEveryDevice(config, areas, _file.FileName);
        var payload = plan.Payload;
        var rows = new List<object>();
        if (areas.Parameters)
        {
            rows.Add(new PreviewHeading(ProvisioningTexts.AreaHeading(ProvisioningTexts.Parameters, payload.Parameters.Count)));
            if (plan.LeftOutParameters > 0)
            {
                rows.Add(new PreviewNote(ProvisioningTexts.LeftOutParameters(plan.LeftOutParameters)));
            }
            else if (payload.Parameters.Count == 0)
            {
                rows.Add(new PreviewNote(ProvisioningTexts.NoParameters));
            }

            rows.AddRange(payload.Parameters.Select(p => new PreviewEntry(p.Name + " = " + (SecretNames.IsSecret(p.Name) ? Hidden : p.Value))));
        }

        if (areas.ActionRules)
        {
            Add(rows, ProvisioningTexts.ActionRules, [.. payload.Rules.Select(r => r.Name)], ProvisioningTexts.NoActionRules);
            if (payload.Schedules.Count > 0)
            {
                Add(rows, ProvisioningTexts.Schedules, [.. payload.Schedules.Select(s => s.Name.Trim().Length > 0 ? s.Name : s.Id)], null);
            }
        }

        if (areas.StreamProfiles)
        {
            Add(rows, ProvisioningTexts.StreamProfiles, [.. payload.StreamProfiles.Select(p => p.Name)], ProvisioningTexts.NoStreamProfiles);
        }

        Rows.ReplaceAll(rows);
        OnPropertyChanged(nameof(HasRows));

        // No area ticked is the areas' own error; else what Configure devices would refuse goes below the file.
        _file.SetContentError(areas is { Parameters: false, ActionRules: false, StreamProfiles: false } || plan.IsValid ? null : plan.Errors[0].Message);
    }

    private static void Add(List<object> rows, string area, IReadOnlyList<string> names, string? none)
    {
        rows.Add(new PreviewHeading(ProvisioningTexts.AreaHeading(area, names.Count)));
        if (names.Count == 0 && none is not null)
        {
            rows.Add(new PreviewNote(none));
        }

        rows.AddRange(names.Select(n => new PreviewEntry(n)));
    }
}
