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
/// the rule turns off. A write changes a real machine: the value is checked before anything is sent, and nothing is read. Only the
/// write that ends a pulse is tried again: it must not leave a relay on.
/// </summary>
public sealed class WriteModbusAction(TimeProvider? time = null) : IActionPlugin
{
    public const string ActionId = "oadm.modbus.write";

    private const string RecipientKey = "recipient";

    /// <summary>What a pulse needs next to its own time: the connect of a first action, the queue of a busy PLC, the off write.</summary>
    private const int PulseExtraSeconds = 45;

    private const string NothingWritten = "Nothing was written.";
    private const string StillOn = "The coil may still be on.";

    /// <summary>The write that ends a pulse, with its tries, on a token of its own: a cancelled run must still turn the coil off.</summary>
    private static readonly TimeSpan OffTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The wait between tries: the first reconnect after a drop takes a second.</summary>
    private static readonly TimeSpan OffRetryWait = TimeSpan.FromSeconds(1);

    /// <summary>Well below the 5 minutes after which an unused session closes.</summary>
    private static readonly TimeSpan SessionUseInterval = TimeSpan.FromSeconds(60);

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
        ActionField.Choice("valueType", "Value type", [new(ModbusValue.OnOff, "On or off"), new(ModbusValue.Pulse, "Pulse"), new(ModbusValue.Number, "Number"), new(ModbusValue.Text, "Text")], ModbusValue.OnOff),
        ActionField.Number("address", "Address", 0, 65535, 0) with { Hint = "Counts from 0" },
        ActionField.Choice("state", "Value", [new("on", "On"), new("off", "Off")], "on") with { VisibleWhenKey = "valueType", VisibleWhenValue = ModbusValue.OnOff },
        ActionField.Number("seconds", "Seconds", 1, 3600, 5) with { VisibleWhenKey = "valueType", VisibleWhenValue = ModbusValue.Pulse },
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
            VisibleWhenKey = "valueType",
            VisibleWhenValue = ModbusValue.Number,
            Hint = "32-bit numbers only",
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
        var valueType = Text(settings, "valueType");
        Check("value", valueType is ModbusValue.Number or ModbusValue.Text);
        Check("offValue", statefulRule);
        if (ModbusValue.AddressProblem(Whole(settings, "address", 0), ModbusValue.Count(valueType, Text(settings, "numberFormat"), Whole(settings, "textLength", 16))) is { } problem)
        {
            errors["address"] = problem;
        }

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
        return Text(settings, "valueType") is ModbusValue.OnOff or ModbusValue.Pulse
            ? string.Create(CultureInfo.InvariantCulture, $"Write coil {address}")
            : string.Create(CultureInfo.InvariantCulture, $"Write register {address}");
    }

    public TimeSpan? MaxRunTime(JsonElement settings) =>
        IsPulse(settings) ? TimeSpan.FromSeconds(Whole(settings, "seconds", 5) + PulseExtraSeconds) : null;

    public bool RunsOn(ActionEdge edge, JsonElement settings) =>
        edge != ActionEdge.TurnedOff || Text(settings, "offValue").Length > 0;

    public async Task ExecuteAsync(IActionContext ctx, ActionInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(invocation);
        var settings = invocation.Settings;
        var address = Whole(settings, "address", 0);
        var pulse = invocation.Edge != ActionEdge.TurnedOff && IsPulse(settings);
        var text = invocation.Edge == ActionEdge.TurnedOff ? Filled(ctx, Text(settings, "offValue"))
            : pulse ? "on"
            : Text(settings, "valueType") == ModbusValue.OnOff ? Text(settings, "state")
            : Filled(ctx, Text(settings, "value"));
        ModbusWrite write;
        try
        {
            write = Encode(settings, text);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{ex.Message} {NothingWritten}", ex);
        }

        if (ModbusValue.AddressProblem(address, write.Coil is null ? write.Registers.Count : 1) is { } problem)
        {
            throw new InvalidOperationException($"{problem} {NothingWritten}");
        }

        var session = await ctx.SessionAsync<IModbusWriter>(RecipientKey, ct).ConfigureAwait(false);
        if (write.Coil is { } state)
        {
            if (pulse)
            {
                await PulseAsync(ctx, session, (ushort)address, TimeSpan.FromSeconds(Whole(settings, "seconds", 5)), ct).ConfigureAwait(false);
            }
            else
            {
                await session.WriteCoilAsync((ushort)address, state, ct).ConfigureAwait(false);
            }

            ctx.Log(string.Create(CultureInfo.InvariantCulture, $"Coil {address}: {(pulse ? "pulse" : state ? "on" : "off")}"));
            return;
        }

        await session.WriteRegistersAsync((ushort)address, write.Registers, ct).ConfigureAwait(false);
        ctx.Log(string.Create(CultureInfo.InvariantCulture, $"Register {address}: {write.Registers.Count} written"));
    }

    /// <summary>The text with its placeholders filled; refuses a name nobody fills and a value that came out empty.</summary>
    private static string Filled(IActionContext ctx, string text)
    {
        if (ActionPlaceholders.FirstUnknown(text, ctx) is { } unknown)
        {
            throw new InvalidOperationException($"The placeholder {unknown} is unknown. {NothingWritten}");
        }

        var filled = ctx.Text(text);
        return filled.Length > 0 || text.Length == 0 ? filled : throw new InvalidOperationException($"The value is empty. {NothingWritten}");
    }

    /// <summary>
    /// On, the wait, off. The off write runs whatever ends the wait, also after an on write that failed without an answer: that one may
    /// have happened.
    /// </summary>
    private async Task PulseAsync(IActionContext ctx, IModbusWriter session, ushort address, TimeSpan length, CancellationToken ct)
    {
        try
        {
            await session.WriteCoilAsync(address, true, ct).ConfigureAwait(false);
            await HoldAsync(ctx, length, ct).ConfigureAwait(false);
        }
        finally
        {
            await TurnOffAsync(ctx, session.Plc, address).ConfigureAwait(false);
        }
    }

    /// <summary>The pulse's wait. Asking for the session keeps it from closing as unused; the end of the pulse never waits for the answer.</summary>
    private async Task HoldAsync(IActionContext ctx, TimeSpan length, CancellationToken ct)
    {
        var end = _time.GetUtcNow() + length;
        while (true)
        {
            var left = end - _time.GetUtcNow();
            await Task.Delay(left < SessionUseInterval ? left : SessionUseInterval, _time, ct).ConfigureAwait(false);
            left = end - _time.GetUtcNow();
            if (left <= TimeSpan.Zero)
            {
                return;
            }

            using var limit = new CancellationTokenSource(left, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, limit.Token);
            try
            {
                await ctx.SessionAsync<IModbusWriter>(RecipientKey, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Still connecting at the end of the pulse: the off write goes on.
            }
            catch (InvalidOperationException)
            {
                // Not connected right now: the off write asks again.
            }
        }
    }

    /// <summary>
    /// The session is asked for again: the one of the on write may have dropped, or the recipient changed. A connection that just
    /// dropped comes back within seconds, so a failed try is repeated while the time allows. A recipient that now leads to another PLC
    /// gets no write: the coil that is on is elsewhere, and a coil turned off there would hide that.
    /// </summary>
    private async Task TurnOffAsync(IActionContext ctx, string plc, ushort address)
    {
        using var limit = new CancellationTokenSource(OffTimeout, _time);
        InvalidOperationException? failed = null;
        while (true)
        {
            var moved = false;
            try
            {
                var session = await ctx.SessionAsync<IModbusWriter>(RecipientKey, limit.Token).ConfigureAwait(false);
                moved = !string.Equals(session.Plc, plc, StringComparison.Ordinal);
                if (!moved)
                {
                    await session.WriteCoilAsync(address, false, limit.Token).ConfigureAwait(false);
                    return;
                }
            }
            catch (ObjectDisposedException ex)
            {
                throw new InvalidOperationException($"The server is stopping. {StillOn}", ex);
            }
            catch (InvalidOperationException ex)
            {
                failed = ex;
            }
            catch (OperationCanceledException ex)
            {
                throw NotOff(failed, ex);
            }

            if (moved)
            {
                throw new InvalidOperationException($"The recipient changed during the pulse. {StillOn}");
            }

            try
            {
                await Task.Delay(OffRetryWait, _time, limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                throw NotOff(failed, ex);
            }
        }
    }

    private static InvalidOperationException NotOff(InvalidOperationException? failed, OperationCanceledException timeout) =>
        new($"{failed?.Message.TrimEnd('.') ?? "The write that turns it off got no answer"}. {StillOn}", (Exception?)failed ?? timeout);

    private static ModbusWrite Encode(JsonElement settings, string value) => ModbusValue.Encode(
        Text(settings, "valueType"),
        value,
        Text(settings, "numberFormat"),
        Text(settings, "wordOrder"),
        Whole(settings, "textLength", 16),
        Text(settings, "byteOrder"));

    private static bool IsPulse(JsonElement settings) =>
        Text(settings, "valueType") == ModbusValue.Pulse;

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
