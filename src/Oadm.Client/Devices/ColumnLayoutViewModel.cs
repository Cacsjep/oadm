using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Client.Infrastructure;

namespace Oadm.Client.Devices;

public sealed partial class ColumnOptionViewModel(string key, string header, bool canHide) : ObservableObject
{
    public string Key { get; } = key;
    public string Header { get; } = header;
    public bool CanHide { get; } = canHide;

    [ObservableProperty] public partial bool IsVisible { get; set; } = true;

    /// <summary>Pixel width, 0 = default from the view.</summary>
    public double Width { get; set; }

    public int DisplayIndex { get; set; }
}

/// <summary>Device grid columns (spec order), visibility for the column chooser, persisted order and widths.</summary>
public sealed class ColumnLayoutViewModel
{
    public const string IconKey = "icon";

    /// <summary>Default columns in the order of the spec table.</summary>
    public static IReadOnlyList<(string Key, string Header)> DefaultColumns { get; } =
    [
        (IconKey, ""),
        ("address", "Address"),
        ("model", "Model"),
        ("status", "Status"),
        ("mac", "MAC address"),
        ("firmware", "Firmware"),
        ("tags", "Tags"),
        ("dhcp", "DHCP"),
        ("https", "HTTPS"),
        ("certExpires", "Certificate expires"),
        ("certTrust", "Certificate"),
        ("dot1x", "IEEE 802.1X"),
    ];

    private readonly IClientSettingsStore _settings;

    public ColumnLayoutViewModel(IClientSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        Dictionary<string, ColumnLayoutEntry> saved = settings.Current.DeviceColumns
            .Where(c => !string.IsNullOrEmpty(c.Key))
            .GroupBy(c => c.Key)
            .ToDictionary(g => g.Key, g => g.First());
        for (int i = 0; i < DefaultColumns.Count; i++)
        {
            (string key, string header) = DefaultColumns[i];
            var option = new ColumnOptionViewModel(key, header, key != IconKey) { DisplayIndex = i };
            if (saved.TryGetValue(key, out ColumnLayoutEntry? entry))
            {
                option.IsVisible = entry.IsVisible || !option.CanHide;
                option.Width = entry.Width;
                option.DisplayIndex = entry.DisplayIndex;
            }

            option.PropertyChanged += (_, _) => Save();
            Columns.Add(option);
        }

        NormalizeOrder();
    }

    /// <summary>Group mode of the device grid (a group per tag), persisted with the layout.</summary>
    public bool GroupByTag
    {
        get => _settings.Current.GroupDevicesByTag;
        set
        {
            if (_settings.Current.GroupDevicesByTag != value)
            {
                _settings.Current.GroupDevicesByTag = value;
                _settings.Save();
            }
        }
    }

    /// <summary>All columns in spec order; <see cref="ColumnOptionViewModel.DisplayIndex"/> holds the user order.</summary>
    public ObservableCollection<ColumnOptionViewModel> Columns { get; } = [];

    /// <summary>Columns the user may hide, for the column chooser.</summary>
    public IEnumerable<ColumnOptionViewModel> Choosable => Columns.Where(c => c.CanHide);

    public ColumnOptionViewModel? Find(string key) => Columns.FirstOrDefault(c => c.Key == key);

    /// <summary>Called by the view after the user reordered or resized columns.</summary>
    public void Capture(IEnumerable<(string Key, int DisplayIndex, double Width)> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool changed = false;
        foreach ((string key, int index, double width) in state)
        {
            ColumnOptionViewModel? option = Find(key);
            if (option is null)
            {
                continue;
            }

            if (option.DisplayIndex != index || Math.Abs(option.Width - width) > 0.5)
            {
                option.DisplayIndex = index;
                option.Width = width;
                changed = true;
            }
        }

        if (changed)
        {
            Save();
        }
    }

    public void ResetToDefault()
    {
        for (int i = 0; i < Columns.Count; i++)
        {
            Columns[i].DisplayIndex = i;
            Columns[i].Width = 0;
            Columns[i].IsVisible = true;
        }

        Save();
    }

    public void Save()
    {
        _settings.Current.DeviceColumns = Columns
            .Select(c => new ColumnLayoutEntry { Key = c.Key, IsVisible = c.IsVisible, Width = c.Width, DisplayIndex = c.DisplayIndex })
            .ToList();
        _settings.Save();
    }

    private void NormalizeOrder()
    {
        List<ColumnOptionViewModel> ordered = Columns.OrderBy(c => c.DisplayIndex).ThenBy(c => Columns.IndexOf(c)).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].DisplayIndex = i;
        }
    }
}
