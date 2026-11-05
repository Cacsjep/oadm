using System.Globalization;
using System.Text.Json;

using Oadm.Sdk.Actions;

namespace Oadm.Plugins.Modbus;

/// <summary>The recipient type of Modbus: a PLC or gateway, one connection the rules share.</summary>
public static class ModbusRecipient
{
    public const string TypeId = "oadm.modbus.recipient";

    public const int DefaultPort = 502;

    public static RecipientType Type { get; } = new(
        TypeId,
        "Modbus",
        "send",
        [
            ActionField.Text("host", "Host", required: true) with { Hint = "Host name or IP address", EmptyText = ActionFormats.EnterHost, Format = ActionFieldFormat.Host },
            ActionField.Number("port", "Port", 1, 65535, DefaultPort),
            ActionField.Number("unitId", "Unit ID", 0, 255, 1),
        ],
        WriteModbusAction.ActionId)
    {
        Target = "{host}:{port}",
        Sessions = new ModbusSessionFactory(),
    };
}

/// <summary>
/// Event rule action "Write Modbus value": one write to a coil or holding registers of a Modbus recipient, with an optional value when
/// the rule turns off. A write changes a real machine: the value is checked before anything is sent, and nothing is read or retried.
/// </summary>
public sealed class WriteModbusAction(TimeProvider? time = null) : IActionPlugin
{
    public const string ActionId = "oadm.modbus.write";

    private const string RecipientKey = "recipient";
    private const string Pulse = "pulse";

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public WriteModbusAction()
        : this(null)
    {
    }

    public string Id => ActionId;

    public string DisplayName => "Write Modbus value";

    public string? IconKey => "send";

    public string Group => "Machines";

    public string? Description => "Writes a value to a PLC or gateway.";

    public bool NeedsDevice => false;

    public bool SupportsTurnOff => true;

    public bool UsesPlaceholders => true;

    public IReadOnlyList<RecipientType> RecipientTypes { get; } = [ModbusRecipient.Type];

    public IReadOnlyList<ActionField> Fields { get; } =
    [
        ActionField.Recipient(RecipientKey, "Recipient", [ModbusRecipient.TypeId]),
        ActionField.Choice("valueType", "Value type", [new(ModbusValue.OnOff, "On or off"), new(ModbusValue.Number, "Number"), new(ModbusValue.Text, "Text")], ModbusValue.OnOff),
        ActionField.Number("address", "Address", 0, 65535, 0) with { Hint = "Counts from 0" },
        ActionField.Choice("state", "Value", [new("on", "On"), new("off", "Off"), new(Pulse, "Pulse")], "on") with { VisibleWhenKey = "valueType", VisibleWhenValue = ModbusValue.OnOff },
        ActionField.Number("seconds", "Seconds", 1, 3600, 5) with { VisibleWhenKey = "state", VisibleWhenValue = Pulse },
        ActionField.Choice("numberFormat", "Number format",
            [
                new(ModbusValue.Signed16, "16-bit signed"),
                new(ModbusValue.Unsigned16, "16-bit unsigned"),
                new(ModbusValue.Signed32, "32-bit signed"),
                new(ModbusValue.Unsigned32, "32-bit unsigned"),
                new(ModbusValue.Decimal32, "32-bit float"),
            ],
            ModbusValue.Unsigned16) with { VisibleWhenKey = "valueType", VisibleWhenValue = ModbusValue.Number },
        ActionField.Choice("wordOrder", "Word order", [new(ModbusValue.HighWordFirst, "High word first"), new(ModbusValue.LowWordFirst, "Low word first")], ModbusValue.HighWordFirst) with
        {
            VisibleWhenKey = "numberFormat",
            VisibleWhenValue = ModbusValue.Signed32 + "|" + ModbusValue.Unsigned32 + "|" + ModbusValue.Decimal32,
        },
        ActionField.Number("textLength", "Length", 1, ModbusValue.MaxTextLength, 16) with { VisibleWhenKey = "valueType", VisibleWhenValue = ModbusValue.Text, Hint = "Characters, two per register" },
        ActionField.Choice("byteOrder", "Byte order", [new(ModbusValue.FirstCharacterHigh, "First character in the high byte"), new(ModbusValue.FirstCharacterLow, "First character in the low byte")], ModbusValue.FirstCharacterHigh) with
        {
            VisibleWhenKey = "valueType",
            VisibleWhenValue = ModbusValue.Text,
        },
        ActionField.Text("value", "Value", required: true) with { VisibleWhenKey = "valueType", VisibleWhenValue = ModbusValue.Number + "|" + ModbusValue.Text, Hint = "A placeholder such as {number} is checked at the run" },
        ActionField.Text("offValue", "Value") with { OffEdge = true, Hint = "Optional" },
    ];

    public IReadOnlyDictionary<string, string> Validate(JsonElement settings, bool statefulRule)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        Check("value", Text(settings, "valueType") != ModbusValue.OnOff);
        Check("offValue", statefulRule);
        return errors;

        void Check(string key, bool applies)
        {
            var value = Text(settings, key);
            if (!applies || value.Length == 0 || value.Contains('{', StringComparison.Ordinal))
            {
                return;
            }

            try
            {
                Encode(settings, value);
            }
            catch (FormatException ex)
            {
                errors[key] = ex.Message;
            }
        }
    }

    public string Describe(JsonElement settings)
    {
        var address = Whole(settings, "address", 0);
        return Text(settings, "valueType") == ModbusValue.OnOff
            ? string.Create(CultureInfo.InvariantCulture, $"Write coil {address}")
            : string.Create(CultureInfo.InvariantCulture, $"Write register {address}");
    }

    public TimeSpan? MaxRunTime(JsonElement settings) =>
        IsPulse(settings) ? TimeSpan.FromSeconds(Whole(settings, "seconds", 5) + 15) : null;

    public bool RunsOn(ActionEdge edge, JsonElement settings) =>
        edge != ActionEdge.TurnedOff || Text(settings, "offValue").Length > 0;

    public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        var settings = invocation.Settings;
        var address = (ushort)Whole(settings, "address", 0);
        var pulse = invocation.Edge != ActionEdge.TurnedOff && IsPulse(settings);
        var text = invocation.Edge == ActionEdge.TurnedOff ? ctx.Text(Text(settings, "offValue"))
            : Text(settings, "valueType") == ModbusValue.OnOff ? (pulse ? "on" : Text(settings, "state"))
            : ctx.Text(Text(settings, "value"));
        ModbusWrite write;
        try
        {
            write = Encode(settings, text);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{ex.Message} Nothing was written.", ex);
        }

        var session = await ctx.SessionAsync<IModbusWriter>(RecipientKey, ct).ConfigureAwait(false);
        if (write.Coil is { } on)
        {
            await session.WriteCoilAsync(address, on, ct).ConfigureAwait(false);
            if (pulse)
            {
                await Task.Delay(TimeSpan.FromSeconds(Whole(settings, "seconds", 5)), _time, ct).ConfigureAwait(false);
                await session.WriteCoilAsync(address, false, ct).ConfigureAwait(false);
            }

            ctx.Log(string.Create(CultureInfo.InvariantCulture, $"Coil {address}: {(pulse ? "pulse" : on ? "on" : "off")}"));
            return;
        }

        await session.WriteRegistersAsync(address, write.Registers, ct).ConfigureAwait(false);
        ctx.Log(string.Create(CultureInfo.InvariantCulture, $"Register {address}: {write.Registers.Count} written"));
    }

    private static ModbusWrite Encode(JsonElement settings, string value) => ModbusValue.Encode(
        Text(settings, "valueType"),
        value,
        Text(settings, "numberFormat"),
        Text(settings, "wordOrder"),
        Whole(settings, "textLength", 16),
        Text(settings, "byteOrder"));

    private static bool IsPulse(JsonElement settings) =>
        Text(settings, "valueType") == ModbusValue.OnOff && Text(settings, "state") == Pulse;

    private static string Text(JsonElement settings, string key) =>
        settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int Whole(JsonElement settings, string key, int fallback)
    {
        if (settings.ValueKind == JsonValueKind.Object && settings.TryGetProperty(key, out var value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return fallback;
    }
}
