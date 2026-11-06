using Oadm.Sdk.Client;

namespace Oadm.Plugins.VapixCommander.Client;

/// <summary>
/// The questions before one request goes to one device, for the page and the request builder alike. The page asks from
/// the command's definition; the server decides again from the filled-in request and can ask for more
/// (<see cref="TryCommandReply.NeedsConfirmation"/>): then the user is asked that question and the request goes again.
/// </summary>
internal static class SendConfirmation
{
    /// <summary>The question for a request that changes the device, or a dangerous one.</summary>
    public static string Text(string name, string address, SafetyLevel level) => level == SafetyLevel.Dangerous
        ? $"\"{name}\" can restart or reset {address} or cut it off the network. Send it now?"
        : $"\"{name}\" changes {address}. Send it now?";

    /// <summary>Why nothing was sent when a confirmation is still missing; null otherwise.</summary>
    public static string? Refusal(TryCommandReply reply) =>
        !reply.NeedsConfirmation ? null : reply.Dangerous ? CommanderTexts.DangerousNotConfirmed : CommanderTexts.NotConfirmed;

    /// <summary>
    /// Sends <paramref name="request"/> with the answers the user gave for <paramref name="asked"/>. When the server sees
    /// more than the page did, the user is asked and the request is sent again. Null: the user said no.
    /// </summary>
    public static async Task<TryCommandReply?> SendAsync(
        ICommanderBackend backend, ICorePluginClientContext host, string name, string address, SafetyLevel asked, TryCommandRequest request)
    {
        request.Confirmed = asked != SafetyLevel.Read;
        request.ConfirmedDangerous = asked == SafetyLevel.Dangerous;
        var reply = await backend.TryAsync(request, CancellationToken.None).ConfigureAwait(true);
        if (!reply.NeedsConfirmation)
        {
            return reply;
        }

        var level = reply.Dangerous ? SafetyLevel.Dangerous : SafetyLevel.Write;
        if (level <= asked)
        {
            return reply; // already answered: the refusal stands
        }

        if (!await host.ConfirmAsync("Send command", Text(name, address, level), "Send").ConfigureAwait(true))
        {
            return null;
        }

        request.Confirmed = true;
        request.ConfirmedDangerous = reply.Dangerous;
        return await backend.TryAsync(request, CancellationToken.None).ConfigureAwait(true);
    }
}
