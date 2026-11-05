using System.Globalization;

using Opc.Ua;

namespace Oadm.Plugins.OpcUa;

/// <summary>What a status of the server or the stack means, in plain words. A status never carries a secret.</summary>
public static class OpcUaTexts
{
    public const string ChooseClientCertificate = "Choose a client certificate.";
    public const string NeedsClientCertificate = "Sign and Sign and encrypt need a client certificate. Choose one.";
    public const string NeedsLoginCertificate = "The Certificate login needs a client certificate. Choose one.";
    public const string NoUriName = "The client certificate has no URI name. Choose one that has.";
    public const string NoPrivateKey = "OADM holds no private key of the client certificate. Choose another one.";
    public const string ModeNotOffered = "The server does not offer this security. Choose another one.";
    public const string PolicyNotOffered = "The server does not offer this policy. Choose another one.";
    public const string ServerCertificateNotTrusted = "OADM does not trust the server's certificate. Choose a Trusted CA or allow untrusted certificates.";
    public const string ConnectionEnded = "The connection ended";
    public const string ServerStopped = "The server stopped";
    public const string SessionClosed = "The session closed";
    public const string NoAnswer = "The server did not answer. The write may have happened.";

    /// <summary>
    /// How the stack's own refusal of an unprotected password begins to differ from a server's: both have the status
    /// BadSecurityChecksFailed, and only the text tells them apart.
    /// </summary>
    private const string PasswordRefusal = "UserName identity token";

    /// <summary>Why a session could not open.</summary>
    /// <param name="status">The status.</param>
    /// <param name="message">The text of the exception.</param>
    /// <param name="endpoint">The address of the server.</param>
    /// <param name="login">The recipient's login.</param>
    public static string ConnectFailed(StatusCode status, string? message, string endpoint, string login)
    {
        if (status == StatusCodes.BadUserAccessDenied || status == StatusCodes.BadIdentityTokenRejected)
        {
            return login == OpcUaRecipient.LoginUser ? "The server refused the login. Check the user name and password." : "The server refused the login.";
        }

        if (status == StatusCodes.BadIdentityTokenInvalid)
        {
            return login == OpcUaRecipient.LoginAnonymous ? "The server does not allow anonymous login. Choose another login." : "The server does not offer this login. Choose another one.";
        }

        if (status == StatusCodes.BadSecurityChecksFailed)
        {
            return message?.Contains(PasswordRefusal, StringComparison.Ordinal) == true
                ? "This password would go out unprotected, and the server's login does not encrypt it. Choose Sign and encrypt."
                : "The server does not trust the client certificate. Trust it on the server.";
        }

        if (status == StatusCodes.BadCertificateUntrusted || status == StatusCodes.BadCertificateChainIncomplete)
        {
            return ServerCertificateNotTrusted;
        }

        if (status == StatusCodes.BadCertificateTimeInvalid)
        {
            return "The server's certificate has expired or is not valid yet.";
        }

        if (status == StatusCodes.BadCertificateHostNameInvalid)
        {
            return "The server's certificate does not name the host of the endpoint. Enter the name it holds, or allow untrusted certificates.";
        }

        if (status == StatusCodes.BadCertificateUriInvalid)
        {
            return "The server's certificate does not hold the server's application URI.";
        }

        if (status == StatusCodes.BadSecurityModeRejected || status == StatusCodes.BadSecurityPolicyRejected)
        {
            return ModeNotOffered;
        }

        return IsUnreachable(status) ? $"{endpoint} cannot be reached." : $"The connection to {endpoint} failed ({Name(status)}).";
    }

    /// <summary>Why a write failed.</summary>
    public static string WriteFailed(StatusCode status)
    {
        if (status == StatusCodes.BadTypeMismatch)
        {
            return "The node has another data type. Choose its value type.";
        }

        if (status == StatusCodes.BadNodeIdUnknown || status == StatusCodes.BadNodeIdInvalid)
        {
            return "The server has no such node.";
        }

        if (status == StatusCodes.BadNotWritable)
        {
            return "The node cannot be written.";
        }

        if (status == StatusCodes.BadUserAccessDenied)
        {
            return "This login may not write the node.";
        }

        if (status == StatusCodes.BadOutOfRange)
        {
            return "The server refused the value: out of range.";
        }

        return IsUnreachable(status) ? NoAnswer : $"The server refused the write ({Name(status)}).";
    }

    private static bool IsUnreachable(StatusCode status) =>
        status == StatusCodes.BadNotConnected
        || status == StatusCodes.BadConnectionClosed
        || status == StatusCodes.BadConnectionRejected
        || status == StatusCodes.BadSecureChannelClosed
        || status == StatusCodes.BadCommunicationError
        || status == StatusCodes.BadServerNotConnected
        || status == StatusCodes.BadServerHalted
        || status == StatusCodes.BadTcpInternalError
        || status == StatusCodes.BadTcpEndpointUrlInvalid
        || status == StatusCodes.BadTimeout
        || status == StatusCodes.BadRequestTimeout
        || status == StatusCodes.BadNoCommunication
        || status == StatusCodes.BadDisconnect;

    private static string Name(StatusCode status) => status.SymbolicId ?? status.Code.ToString("X8", CultureInfo.InvariantCulture);
}
