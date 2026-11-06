namespace Oadm.Sdk.Client;

/// <summary>Server errors for plugin pages without a gRPC reference.</summary>
public static class ServerErrors
{
    /// <summary>The user message of a failed server call: the gRPC Status.Detail, else the exception's message.</summary>
    public static string Message(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var detail = ex.GetType().GetProperty("Status")?.GetValue(ex) is { } status
            ? status.GetType().GetProperty("Detail")?.GetValue(status) as string
            : null;
        return string.IsNullOrEmpty(detail) ? ex.Message : detail;
    }

    /// <summary>
    /// A call that was cancelled: an <see cref="OperationCanceledException"/>, or the gRPC error of a cancelled call (the
    /// host's channel throws that one, status Cancelled, when the token of a call is cancelled).
    /// </summary>
    internal static bool IsCancellation(Exception ex) =>
        ex is OperationCanceledException
        || (ex.GetType().GetProperty("StatusCode")?.GetValue(ex) is Enum code && code.ToString() == "Cancelled");
}
