using System.Globalization;

using Grpc.Core;

using Oadm.Contracts.Logging;
using Oadm.Core.Persistence;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Logging;

/// <summary>
/// gRPC ServerLogService: the server's log files for the Logs page, a page at a time, newest first (permission
/// "logs.server", <see cref="Core.Auth.HostPermissions.LogsServer"/>). The files are read on the request, nothing is kept.
/// </summary>
public sealed class ServerLogGrpcService(OadmPaths paths) : Proto.ServerLogService.ServerLogServiceBase
{
    /// <summary>The server's log files (<c>oadm-server-20261017.log</c>, <c>..._001.log</c>).</summary>
    public const string FilePattern = "oadm-server-*.log";

    public override async Task<Proto.ServerLogPage> List(Proto.ListServerLogRequest request, ServerCallContext context)
    {
        var minRank = MinRank(request.MinLevel);
        var limit = request.Limit <= 0 ? 200 : request.Limit;
        var notAfter = ParseTime(request.NotAfter);
        var notBefore = ParseTime(request.NotBefore);
        var page = await Task.Run(
            () => LogFileReader.Read(paths.LogsDirectory, FilePattern, request.Offset, limit, minRank, request.Search, context.CancellationToken, notAfter, notBefore),
            context.CancellationToken).ConfigureAwait(false);
        var reply = new Proto.ServerLogPage { HasMore = page.HasMore };
        reply.Entries.AddRange(page.Entries.Select(e => new Proto.ServerLogEntry
        {
            Time = e.Time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
            Level = e.Level,
            Source = e.Source,
            Message = e.Message,
            Exception = e.Exception,
        }));
        return reply;
    }

    /// <summary>An ISO 8601 time of a request (not_after, not_before); empty = none; INVALID_ARGUMENT when it is no time.</summary>
    internal static DateTimeOffset? ParseTime(string text) =>
        string.IsNullOrEmpty(text) ? null
        : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time
        : throw new RpcException(new Status(StatusCode.InvalidArgument, "Enter a time like 2026-11-14T09:30:00Z."));

    /// <summary>The lowest rank of a min_level ("Information", "Warning", "Error"; empty = all).</summary>
    internal static int MinRank(string level) => string.IsNullOrEmpty(level) ? 0 : LogFileReader.Rank(level);
}
