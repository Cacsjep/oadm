namespace Oadm.Core.MyAxis;

/// <summary>
/// The MyAxis sign-in as the server keeps it (docs/spec/myaxis.md): the one cookie <c>axis_connect_session_sid</c> of
/// www.axis.com as "name=value", sent as the one <c>Cookie</c> header. A secret like a device password: never logged, never
/// in an answer.
/// </summary>
public static class MyAxisCookies
{
    /// <summary>The session cookie of www.axis.com.</summary>
    public const string SessionName = "axis_connect_session_sid";

    /// <summary>The longest "name=value" kept.</summary>
    public const int MaxLength = 4096;

    /// <summary>The <c>INVALID_ARGUMENT</c> text of anything else.</summary>
    public const string InvalidText = "The MyAxis sign-in could not be read. Try again.";

    /// <summary>
    /// True for exactly one pair <c>axis_connect_session_sid=&lt;value&gt;</c> of at most <see cref="MaxLength"/> characters,
    /// the value not empty and visible ASCII without <c>; , " \</c> or spaces.
    /// </summary>
    public static bool IsValid(string? session)
    {
        const string prefix = SessionName + "=";
        if (session is null || session.Length > MaxLength || session.Length <= prefix.Length
            || !session.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in session.AsSpan(prefix.Length))
        {
            if (c is <= ' ' or > '~' or ';' or ',' or '"' or '\\')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// A user's MyAxis sign-in (table MyAxisSessions, one row per user, deleted with the user). <see cref="EncryptedSession"/> is
/// the cookie encrypted with the master key and the user id as associated data; null after axis.com refused it
/// (<see cref="Expired"/>). Never in a backup.
/// </summary>
public sealed class MyAxisSessionEntity
{
    public Guid UserId { get; set; }

    /// <summary>AES-256-GCM of the cookie (<see cref="Security.CredentialProtector"/>); null once expired.</summary>
    public byte[]? EncryptedSession { get; set; }

    public DateTime SignedInUtc { get; set; }

    /// <summary>The last time axis.com accepted it (the sign-in check or a download).</summary>
    public DateTime LastAcceptedUtc { get; set; }

    /// <summary>axis.com refused the kept session; cleared by a new sign-in, the row goes with a sign-out.</summary>
    public bool Expired { get; set; }

    public override string ToString() => $"MyAxisSession {UserId} (signed in {SignedInUtc:u}, expired {Expired})";
}
