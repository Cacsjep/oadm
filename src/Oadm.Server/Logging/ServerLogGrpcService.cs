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
        var minRank = string.IsNullOrEmpty(request.MinLevel) ? 0 : LogFileReader.Rank(request.MinLevel);
        var limit = request.Limit <= 0 ? 200 : request.Limit;
        var page = await Task.Run(
            () => LogFileReader.Read(paths.LogsDirectory, FilePattern, request.Offset, limit, minRank, request.Search, context.CancellationToken),
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
}
