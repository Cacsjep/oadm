using Grpc.Core;

using Oadm.Core.Uploads;
using Oadm.Server.Common;
using Oadm.Server.Mapping;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Files;

/// <summary>gRPC FileService: client uploads (firmware, ACAP packages) into the <see cref="UploadStore"/>.</summary>
public sealed partial class FileGrpcService(UploadStore store, ILogger<FileGrpcService> logger) : Proto.FileService.FileServiceBase
{
    /// <summary>Header first, then data chunks. Partial uploads are deleted when the stream fails.</summary>
    public override async Task<Proto.UploadedFileInfo> Upload(IAsyncStreamReader<Proto.UploadChunk> requestStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(requestStream);
        var ct = context.CancellationToken;
        if (!await requestStream.MoveNext(ct).ConfigureAwait(false) || requestStream.Current.PartCase != Proto.UploadChunk.PartOneofCase.Header)
        {
            throw GrpcGuard.InvalidArgument("An upload must start with a header (name and size).");
        }

        var header = requestStream.Current.Header;
        try
        {
            await using var writer = await store.BeginAsync(header.Name, header.Size, ct).ConfigureAwait(false);
            while (await requestStream.MoveNext(ct).ConfigureAwait(false))
            {
                var chunk = requestStream.Current;
                if (chunk.PartCase != Proto.UploadChunk.PartOneofCase.Data)
                {
                    throw GrpcGuard.InvalidArgument("Only one header is allowed per upload.");
                }

                await writer.WriteAsync(chunk.Data.Memory, ct).ConfigureAwait(false);
            }

            var file = await writer.CompleteAsync(ct).ConfigureAwait(false);
            var owner = GrpcGuard.Owner(context);
            LogUploaded(file.Id, file.Size, owner);
            return Mappers.ToProto(file);
        }
        catch (UploadRejectedException ex)
        {
            throw new RpcException(new Status(ex.TooLarge ? StatusCode.ResourceExhausted : StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<Proto.Empty> Delete(Proto.FileIdRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!UploadStore.IsValidId(request.FileId))
        {
            throw GrpcGuard.InvalidArgument($"file id '{request.FileId}' is not a valid id.");
        }

        if (!await store.DeleteAsync(request.FileId, context.CancellationToken).ConfigureAwait(false))
        {
            throw GrpcGuard.NotFound($"Upload {request.FileId} not found.");
        }

        return new Proto.Empty();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Upload {FileId} received ({Size} bytes) from {Owner}")]
    private partial void LogUploaded(string fileId, long size, string owner);
}
