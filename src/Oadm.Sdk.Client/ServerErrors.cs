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
}
