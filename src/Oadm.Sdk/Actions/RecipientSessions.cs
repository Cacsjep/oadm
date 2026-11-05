using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace Oadm.Sdk.Actions;

/// <summary>
/// One open connection of a persistent recipient (SDK 0.12.0, MQTT, Modbus, OPC UA), shared by every rule that chooses the
/// recipient. What it can send is the plugin's own interface: an action asks for it with
/// <see cref="IActionContext.SessionAsync{T}"/>, and the host never knows the protocol. Several actions use a session at the
/// same time, so a protocol that sends one request at a time keeps its own order.
/// </summary>
public interface IRecipientSession : IAsyncDisposable
{
    /// <summary>Raised once when the connection ended by itself; the host opens a new session after a wait.</summary>
    event EventHandler<RecipientDroppedEventArgs>? Dropped;

    /// <summary>Opens the connection; throws with a plain reason when it cannot. The login is checked here.</summary>
    Task ConnectAsync(CancellationToken ct);
}

/// <summary>Why a session ended.</summary>
/// <param name="reason">A short plain reason, never a secret.</param>
public sealed class RecipientDroppedEventArgs(string reason) : EventArgs
{
    public string Reason { get; } = reason;
}

/// <summary>Makes the sessions of one <see cref="RecipientType"/>. Set it as <see cref="RecipientType.Sessions"/>.</summary>
public interface IRecipientSessionFactory
{
    /// <summary>A session that is not connected yet; called once per connection attempt.</summary>
    IRecipientSession Create(RecipientSessionRequest request);
}

/// <summary>What a session is made from.</summary>
/// <param name="Settings">The recipient's fields over their defaults, passwords decrypted. Stays inside the session.</param>
/// <param name="Certificates">
/// Per <see cref="ActionFieldType.ClientCertificate"/> or <see cref="ActionFieldType.TrustedCertificate"/> field that is visible and
/// has a value: the chosen certificate first, then its issuers. A client certificate holds its private key.
/// </param>
/// <param name="Logger">The server log.</param>
public sealed record RecipientSessionRequest(
    JsonElement Settings,
    IReadOnlyDictionary<string, IReadOnlyList<X509Certificate2>> Certificates,
    ILogger Logger)
{
    /// <summary>The recipient; null for a recipient's Test, which runs on the values of its form beside the open session.</summary>
    public Guid? RecipientId { get; init; }

    /// <summary>A setting as text (null when missing or null).</summary>
    public string? Get(string key) =>
        Settings.ValueKind == JsonValueKind.Object && Settings.TryGetProperty(key, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => value.GetRawText(),
            }
            : null;

    /// <summary>A setting as a number.</summary>
    public int GetInt(string key, int fallback = 0) =>
        int.TryParse(Get(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : fallback;

    /// <summary>A setting as on or off.</summary>
    public bool GetBool(string key) => string.Equals(Get(key), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The certificates of a field; empty without one.</summary>
    public IReadOnlyList<X509Certificate2> CertificatesOf(string key) =>
        Certificates.TryGetValue(key, out var chain) ? chain : [];
}

/// <summary>The texts of sessions, the same for every protocol.</summary>
public static class RecipientSessionTexts
{
    /// <summary>A certificate the recipient names that is gone.</summary>
    public const string CertificateGone = "The chosen certificate no longer exists. Choose another one.";

    /// <summary>The result of a recipient's Test.</summary>
    public const string Connected = "Connected";

    /// <summary>A session that is opening.</summary>
    public const string Connecting = "Connecting";

    /// <summary>An action on a session that is not connected.</summary>
    public static string NotConnected(string? reason) => string.IsNullOrWhiteSpace(reason) ? "Not connected" : "Not connected: " + reason;
}
