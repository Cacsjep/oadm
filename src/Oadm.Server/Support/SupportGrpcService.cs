using Grpc.Core;

using Oadm.Core.Auth;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Support;

/// <summary>gRPC SupportService: the support report (Administrator only, audited). See <see cref="SupportReport"/>.</summary>
public sealed class SupportGrpcService(SupportReport report, AuditLog audit) : Proto.SupportService.SupportServiceBase
{
    private const int ChunkBytes = 256 * 1024;

    public override async Task CreateReport(Proto.Empty request, IServerStreamWriter<Proto.DownloadChunk> responseStream, ServerCallContext context)
    {
        var ct = context.CancellationToken;
        var (path, name) = await report.CreateAsync(ct).ConfigureAwait(false);
        try
        {
            await audit.WriteAsync(AuditActions.SupportReportCreated, name, null, ct).ConfigureAwait(false);

            // A ZIP does not compress again.
            context.WriteOptions = new WriteOptions(WriteFlags.NoCompress);
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, useAsync: true);
            await using (file.ConfigureAwait(false))
            {
                await responseStream.WriteAsync(new Proto.DownloadChunk { FileName = name, Length = file.Length }, ct).ConfigureAwait(false);
                var buffer = new byte[ChunkBytes];
                int read;
                while ((read = await file.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await responseStream.WriteAsync(new Proto.DownloadChunk { Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read) }, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }
}
