using Grpc.Core;

namespace Oadm.Server.Common;

/// <summary>Small helpers to turn bad input into gRPC status codes.</summary>
internal static class GrpcGuard
{
    public const string OwnerHeader = "oadm-owner";

    public static Guid ParseId(string? value, string field)
    {
        if (!Guid.TryParse(value, out var id))
        {
            throw InvalidArgument($"{field} '{value}' is not a valid id.");
        }

        return id;
    }

    public static Guid[] ParseIds(IEnumerable<string> values, string field) =>
        [.. values.Select(v => ParseId(v, field)).Distinct()];

    public static RpcException InvalidArgument(string message) => new(new Status(StatusCode.InvalidArgument, message));

    public static RpcException NotFound(string message) => new(new Status(StatusCode.NotFound, message));

    public static RpcException FailedPrecondition(string message) => new(new Status(StatusCode.FailedPrecondition, message));

    /// <summary>
    /// Who started an action: the <c>oadm-owner</c> request header (client sends "machine/user"),
    /// else the explicit value, else the peer address.
    /// </summary>
    public static string Owner(ServerCallContext context, string? explicitOwner = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!string.IsNullOrWhiteSpace(explicitOwner))
        {
            return explicitOwner.Trim();
        }

        var header = context.RequestHeaders.GetValue(OwnerHeader);
        return !string.IsNullOrWhiteSpace(header) ? header.Trim() : context.Peer ?? "unknown";
    }

    /// <summary>Cancelled when the call ends or the server shuts down, so long streams never block shutdown.</summary>
    public static CancellationTokenSource LinkWithShutdown(ServerCallContext context, CancellationToken stopping) =>
        CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, stopping);
}
