using System.Collections.ObjectModel;

using CommunityToolkit.Mvvm.ComponentModel;

using Oadm.Sdk.Network;

namespace Oadm.Sdk.Client.Network;

/// <summary>
/// The "Listen on" select of a network service page (NTP server, DHCP server): the server's interfaces as the server
/// lists them, the selection kept across refreshes, and a configured interface that is gone stays selectable as
/// "Ethernet (not available)" so the user sees what is configured. Bind a ComboBox to <see cref="Items"/> and
/// <see cref="Selected"/> (item text: <see cref="InterfaceOption.Label"/>).
/// </summary>
public sealed partial class InterfaceSelection : ObservableObject
{
    public ObservableCollection<InterfaceOption> Items { get; } = [];

    [ObservableProperty]
    public partial InterfaceOption? Selected { get; set; }

    /// <summary>Id of the selected interface, null when nothing is selected.</summary>
    public string? SelectedId => Selected?.Id;

    /// <summary>
    /// Replaces the list (a refresh). The current selection, else <paramref name="configuredId"/>, stays selected;
    /// a configured interface missing from the list is added as "(not available)". Empty lists are ignored.
    /// </summary>
    public void Apply(IReadOnlyList<InterfaceOption> options, string? configuredId, string? configuredName)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count == 0)
        {
            return;
        }

        var selectedId = Selected?.Id ?? configuredId;
        Items.Clear();
        foreach (var option in options)
        {
            Items.Add(option);
        }

        if (selectedId is not null && !options.Any(o => string.Equals(o.Id, selectedId, StringComparison.Ordinal))
            && string.Equals(selectedId, configuredId, StringComparison.Ordinal))
        {
            var name = configuredName ?? configuredId!;
            Items.Add(new InterfaceOption(configuredId!, $"{name} (not available)", name, []));
        }

        Selected = Find(selectedId) ?? Items[0];
    }

    /// <summary>Selects the interface with this id (the form was loaded or saved); the first one when it is not listed.</summary>
    public void Select(string? id) => Selected = Find(id) ?? Items.FirstOrDefault();

    public InterfaceOption? Find(string? id) => id is null ? null : Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    partial void OnSelectedChanged(InterfaceOption? value) => OnPropertyChanged(nameof(SelectedId));
}
