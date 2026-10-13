using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Backup;
using Oadm.Core.Uploads;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Backup;

/// <summary>gRPC BackupService (Administrator only, every change audited). See <see cref="BackupManager"/>.</summary>
public sealed class BackupGrpcService(BackupManager backups, UploadStore uploads, AuditLog audit) : Proto.BackupService.BackupServiceBase
{
    private const int ChunkBytes = 256 * 1024;

    public override async Task<Proto.BackupState> GetState(Proto.Empty request, ServerCallContext context) =>
        await StateAsync(context.CancellationToken).ConfigureAwait(false);

    public override async Task<Proto.BackupState> SetSchedule(Proto.BackupSchedule request, ServerCallContext context)
    {
        try
        {
            var saved = await backups.SetScheduleAsync(request.Enabled, request.TimeOfDay, request.Keep, request.Password, context.CancellationToken).ConfigureAwait(false);
            var detail = saved.Enabled ? $"On, daily at {saved.TimeOfDay}, keep {saved.Keep}" : "Off";
            await audit.WriteAsync(AuditActions.BackupScheduleChanged, "Automatic backups", detail + (string.IsNullOrEmpty(request.Password) ? "" : ", new password"), context.CancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }

        return await StateAsync(context.CancellationToken).ConfigureAwait(false);
    }

    public override async Task Create(Proto.CreateBackupRequest request, IServerStreamWriter<Proto.DownloadChunk> responseStream, ServerCallContext context)
    {
        if (BackupFile.PasswordProblem(request.Password) is { } problem)
        {
            throw GrpcGuard.InvalidArgument(problem);
        }

        // Made into a file on the server first: the client gets the length, and a failure never leaves half a backup there.
        var ct = context.CancellationToken;
        var temp = Path.Combine(backups.Folder, "tmp", Guid.NewGuid().ToString("N") + ".part");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            BackupContents contents;
            var file = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, useAsync: true);
            await using (file.ConfigureAwait(false))
            {
                contents = await backups.CreateAsync(file, request.Password, ct).ConfigureAwait(false);
            }

            var name = backups.DownloadName(contents.Header.ServerName);
            await audit.WriteAsync(AuditActions.BackupCreated, name, string.Join("; ", BackupManager.Lines(contents.Manifest.Counts, contents.Manifest.PluginData.Count, restored: false)), ct).ConfigureAwait(false);
            await SendFileAsync(temp, name, responseStream, context).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public override async Task Download(Proto.BackupName request, IServerStreamWriter<Proto.DownloadChunk> responseStream, ServerCallContext context)
    {
        var path = backups.PathOf(request.Name) ?? throw GrpcGuard.NotFound("There is no such backup on the server.");
        await audit.WriteAsync(AuditActions.BackupDownloaded, request.Name, null, context.CancellationToken).ConfigureAwait(false);
        await SendFileAsync(path, request.Name, responseStream, context).ConfigureAwait(false);
    }

    public override async Task<Proto.BackupState> Delete(Proto.BackupName request, ServerCallContext context)
    {
        if (!backups.Delete(request.Name))
        {
            throw GrpcGuard.NotFound("There is no such backup on the server.");
        }

        await audit.WriteAsync(AuditActions.BackupDeleted, request.Name, null, context.CancellationToken).ConfigureAwait(false);
        return await StateAsync(context.CancellationToken).ConfigureAwait(false);
    }

    public override async Task<Proto.BackupInfo> Inspect(Proto.RestoreRequest request, ServerCallContext context)
    {
        var (open, _) = await SourceAsync(request, context.CancellationToken).ConfigureAwait(false);
        var inspection = await backups.InspectAsync(open, request.Password, context.CancellationToken).ConfigureAwait(false);
        var info = new Proto.BackupInfo { WrongPassword = inspection.WrongPassword, Problem = inspection.Problem ?? string.Empty };
        if (inspection.Header is { } header)
        {
            info.OadmVersion = header.OadmVersion;
            info.ServerName = header.ServerName;
            info.Kind = header.Kind;
            info.Created = Timestamp.FromDateTimeOffset(header.CreatedUtc);
        }

        info.Lines.AddRange(inspection.Lines);
        return info;
    }

    public override async Task<Proto.RestoreReply> Restore(Proto.RestoreRequest request, ServerCallContext context)
    {
        var (open, source) = await SourceAsync(request, context.CancellationToken).ConfigureAwait(false);
        var caller = CallerContext.Current;
        try
        {
            var safety = await backups.RestoreAsync(
                open,
                source,
                request.Password,
                caller?.Owner ?? GrpcGuard.Owner(context),
                caller?.ClientAddress ?? context.Peer,
                context.CancellationToken).ConfigureAwait(false);
            if (request.SourceCase == Proto.RestoreRequest.SourceOneofCase.FileId)
            {
                await uploads.DeleteAsync(request.FileId, CancellationToken.None).ConfigureAwait(false);
            }

            return new Proto.RestoreReply
            {
                Message = "The server restarts and restores the backup. Log in again in a minute.",
                SafetyBackup = safety,
            };
        }
        catch (BackupRefusedException ex)
        {
            throw GrpcGuard.FailedPrecondition(ex.Message);
        }
    }

    /// <summary>Opens the backup to restore: an upload of the client or a backup kept on the server; and its name.</summary>
    private async Task<(Func<CancellationToken, Task<Stream>> Open, string Name)> SourceAsync(Proto.RestoreRequest request, CancellationToken ct)
    {
        switch (request.SourceCase)
        {
            case Proto.RestoreRequest.SourceOneofCase.FileId:
                var upload = await uploads.FindAsync(request.FileId, ct).ConfigureAwait(false) ?? throw GrpcGuard.NotFound("The uploaded backup is no longer on the server. Choose it again.");
                return (c => uploads.OpenReadAsync(request.FileId, c), upload.Name);
            case Proto.RestoreRequest.SourceOneofCase.Name:
                var path = backups.PathOf(request.Name) ?? throw GrpcGuard.NotFound("There is no such backup on the server.");
                return (_ => Task.FromResult<Stream>(File.OpenRead(path)), request.Name);
            default:
                throw GrpcGuard.InvalidArgument("Choose a backup.");
        }
    }

    private static async Task SendFileAsync(string path, string name, IServerStreamWriter<Proto.DownloadChunk> responseStream, ServerCallContext context)
    {
        // Encrypted data does not compress.
        context.WriteOptions = new WriteOptions(WriteFlags.NoCompress);
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, useAsync: true);
        await using (file.ConfigureAwait(false))
        {
            await responseStream.WriteAsync(new Proto.DownloadChunk { FileName = name, Length = file.Length }).ConfigureAwait(false);
            var buffer = new byte[ChunkBytes];
            int read;
            while ((read = await file.ReadAsync(buffer, context.CancellationToken).ConfigureAwait(false)) > 0)
            {
                await responseStream.WriteAsync(new Proto.DownloadChunk { Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, read) }).ConfigureAwait(false);
            }
        }
    }

    private async Task<Proto.BackupState> StateAsync(CancellationToken ct)
    {
        var schedule = await backups.GetScheduleAsync(ct).ConfigureAwait(false);
        var state = new Proto.BackupState
        {
            Schedule = new Proto.BackupSchedule { Enabled = schedule.Enabled, TimeOfDay = schedule.TimeOfDay, Keep = schedule.Keep },
            HasPassword = schedule.ProtectedPassword is not null,
            Folder = backups.Folder,
            ActiveTasks = backups.ActiveTasks,
        };
        state.Backups.AddRange(backups.List().Select(b => new Proto.ServerBackup
        {
            Name = b.Name, Size = b.Size, Created = Timestamp.FromDateTimeOffset(b.CreatedUtc), Kind = b.Kind,
        }));
        if (await backups.GetLastAutomaticAsync(ct).ConfigureAwait(false) is { } last)
        {
            state.LastAutomatic = Timestamp.FromDateTimeOffset(last.TimeUtc);
            state.LastAutomaticError = last.Error ?? string.Empty;
        }

        if (await backups.GetLastRestoreAsync(ct).ConfigureAwait(false) is { } restore)
        {
            state.LastRestore = new Proto.RestoreReport
            {
                Source = restore.Source,
                Applied = Timestamp.FromDateTimeOffset(restore.AppliedUtc),
                FromVersion = restore.FromVersion,
                RequestedBy = restore.RequestedBy,
                Error = restore.Error ?? string.Empty,
            };
            state.LastRestore.Lines.AddRange(restore.Lines);
        }

        return state;
    }
}
