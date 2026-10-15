using System.Security.Cryptography;
using System.Text;

using Oadm.Plugins.ActionRules.Device;

namespace Oadm.Plugins.ActionRules.Rules;

/// <summary>
/// What makes two rules, recipients or schedules the same (ids and configuration ids aside), and the version the page gets.
/// The content includes passwords, so it never leaves the server: the version is an HMAC with a key that exists only in
/// this server process (a new one after a restart, so the page reads again), and comparisons on the server use the
/// content itself.
/// </summary>
public static class RuleContent
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    public static string Of(DeviceRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var text = new StringBuilder()
            .Append(rule.Name).Append('\u0001').Append(rule.Enabled).Append('\u0001')
            .Append(RuleText.TriggerKey(rule.StartEvent, rule.Conditions, rule.Timeout)).Append('\u0001')
            .Append(rule.Action.Template);
        Append(text, rule.Action.Parameters);
        return text.ToString();
    }

    public static string Of(DeviceRecipient recipient)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        var text = new StringBuilder().Append(recipient.Name).Append('\u0001').Append(recipient.Template);
        Append(text, recipient.Parameters);
        return text.ToString();
    }

    public static string Of(DeviceSchedule schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        return schedule.Id + "\u0001" + schedule.Name + "\u0001" + ScheduleText.Normalize(schedule.Definition);
    }

    public static bool Same(DeviceRule a, DeviceRule b) => Of(a) == Of(b);

    public static bool Same(DeviceRecipient a, DeviceRecipient b) => Of(a) == Of(b);

    public static bool Same(DeviceSchedule a, DeviceSchedule b) => Of(a) == Of(b);

    /// <summary>The page's version of some content: keyed, so a client cannot test guessed passwords against it.</summary>
    public static string Version(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexString(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(content)), 0, 12);
    }

    private static void Append(StringBuilder text, IEnumerable<NameValue> parameters)
    {
        foreach (var p in parameters.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            text.Append('\u0001').Append(p.Name).Append('=').Append(p.Value);
        }
    }
}
