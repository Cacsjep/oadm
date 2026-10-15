using System.Globalization;

using Oadm.Plugins.ActionRules.Rules;

namespace Oadm.Plugins.ActionRules.Client;

/// <summary>One row of the upper grid: a group of rules, or the devices that could not be read.</summary>
public sealed class RuleGroupRow
{
    private RuleGroupRow(RuleGroup? group, IReadOnlyList<DeviceRulesRow> problems)
    {
        Group = group;
        Problems = problems;
        SearchText = group is null
            ? "not read " + string.Join(' ', problems.Select(p => p.Address + " " + p.Text))
            : string.Join(' ', group.NamesText, group.TriggerText, group.ActionText, group.ValuesText, string.Join(' ', group.Members.Select(m => m.Rule.Name)));
    }

    public static RuleGroupRow For(RuleGroup group) => new(group, []);

    public static RuleGroupRow NotRead(IReadOnlyList<DeviceRulesRow> devices) => new(null, devices);

    public RuleGroup? Group { get; }

    public IReadOnlyList<DeviceRulesRow> Problems { get; }

    public bool IsProblem => Group is null;

    public string Key => Group?.Key ?? "~not-read";

    public string NamesText => Group?.NamesText ?? "Not read";

    public string TriggerText => Group?.TriggerText ?? string.Join(", ", Problems.Take(3).Select(p => p.Address)) + (Problems.Count > 3 ? ", …" : string.Empty);

    public string ActionText => Group?.ActionText ?? string.Empty;

    public string OnText => Group?.OnText ?? string.Empty;

    public string ValuesText => Group?.ValuesText ?? string.Empty;

    public int DeviceCount => Group?.DeviceCount ?? Problems.Count;

    public string DevicesText => DeviceCount.ToString("N0", CultureInfo.CurrentCulture);

    public string SearchText { get; }
}

/// <summary>One row of the lower grid: a rule of one device, or a device that could not be read.</summary>
public sealed class RuleMemberRow
{
    public RuleMemberRow(RuleMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        Member = member;
        Device = member.Device;
        AddressSortKey = AddressKey(Device.Address);
    }

    public RuleMemberRow(DeviceRulesRow problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        Device = problem;
        AddressSortKey = AddressKey(problem.Address);
    }

    public RuleMember? Member { get; }

    public DeviceRulesRow Device { get; }

    public string Address => Device.Address;

    public string AddressSortKey { get; }

    public string Model => Device.Model ?? string.Empty;

    public string Firmware => Device.Firmware ?? string.Empty;

    public string RuleName => Member?.Rule.Name ?? string.Empty;

    public bool IsOn => Member?.Rule.Enabled == true;

    public string OnText => Member is null ? string.Empty : IsOn ? "On" : "Off";

    public string ActionText => Member is null ? string.Empty : ActionNames.Of(Member.Rule.Template);

    public string TriggerText => Member is null ? string.Empty : RuleGrouping.TriggerText(Member.Rule, Device);

    /// <summary>"host 192.168.1.5 · message camera 17"; passwords as "password set".</summary>
    public string ValuesText => Member is null
        ? string.Empty
        : string.Join(" · ", Member.Rule.Parameters.Select(p => RuleText.IsSecret(p.Name)
            ? p.Name + (Member.Rule.StoredSecrets.Contains(p.Name) ? " set" : " empty")
            : p.Name + " " + (p.Value.Length == 0 ? "(empty)" : p.Value)));

    public string ChangeText => Member is null ? string.Empty : Device.InPlace ? "In place" : "Recreated";

    public bool IsProblem => Member is null;

    public string ProblemText => Device.Text ?? string.Empty;

    /// <summary>IPv4 sorted numerically ("010.000.000.048"), names as they are.</summary>
    public static string AddressKey(string address) =>
        System.Net.IPAddress.TryParse(address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? string.Join('.', ip.GetAddressBytes().Select(b => b.ToString("000", CultureInfo.InvariantCulture)))
            : "~" + address;
}
