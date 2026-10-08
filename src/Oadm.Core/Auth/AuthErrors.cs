namespace Oadm.Core.Auth;

/// <summary>Why an auth or user operation was refused; the gRPC layer maps it to a status code.</summary>
public enum AuthError
{
    /// <summary>Wrong user name or password, disabled user, or an unknown / expired token (UNAUTHENTICATED).</summary>
    NotAuthenticated,

    /// <summary>Too many failed logins (RESOURCE_EXHAUSTED).</summary>
    Locked,

    /// <summary>Wrong setup code or not allowed from this client (PERMISSION_DENIED).</summary>
    PermissionDenied,

    /// <summary>A rule forbids it: users exist already, last administrator, own account (FAILED_PRECONDITION).</summary>
    Precondition,

    /// <summary>Invalid user name, password or role (INVALID_ARGUMENT).</summary>
    InvalidArgument,

    /// <summary>Unknown user (NOT_FOUND).</summary>
    NotFound,
}

/// <summary>A refused auth or user operation; <see cref="Exception.Message"/> is the text for the user.</summary>
public sealed class AuthException : Exception
{
    public AuthException()
    {
    }

    public AuthException(string message)
        : base(message)
    {
    }

    public AuthException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AuthException(AuthError error, string message)
        : base(message)
    {
        Error = error;
    }

    public AuthError Error { get; }
}
