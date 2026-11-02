using Grpc.Core;

using Microsoft.Extensions.Logging;

namespace Oadm.Client.MyAxis;

/// <summary>The log lines of the MyAxis sign-in: status codes only, never the session.</summary>
internal static partial class MyAxisLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "The MyAxis sign-in could not be read: {Status}")]
    public static partial void ReadFailed(ILogger logger, StatusCode status);

    [LoggerMessage(Level = LogLevel.Information, Message = "MyAxis sign-out failed: {Status}")]
    public static partial void SignOutFailed(ILogger logger, StatusCode status);

    [LoggerMessage(Level = LogLevel.Information, Message = "MyAxis sign-in failed: {Status}")]
    public static partial void SignInFailed(ILogger logger, StatusCode status);

    [LoggerMessage(Level = LogLevel.Information, Message = "MyAxis did not accept the sign-in after {Attempts} tries")]
    public static partial void NotAccepted(ILogger logger, int attempts);
}
