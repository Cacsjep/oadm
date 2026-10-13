using System.Text;

using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>
/// Fake BackupService so <c>--fake</c> shows the Settings page's backup card: two backups kept on the server, a schedule,
/// the password "wrong" is refused, a backup "from a newer OADM" is any file named "future*", and a restore only answers
/// (nothing restarts).
/// </summary>
public sealed partial class FakeOadmApi
{
    private BackupSchedule _backupSchedule = new() { Enabled = true, TimeOfDay = "02:00", Keep = 7 };
    private readonly List<ServerBackup> _serverBackups =
    [
        new() { Name = "oadm-backup-20261013-020000-automatic.oadmbackup", Size = 18_734_221, Created = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)), Kind = "automatic" },
        new() { Name = "oadm-backup-20261012-020000-automatic.oadmbackup", Size = 18_701_002, Created = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero)), Kind = "automatic" },
    ];

    /// <summary>What <see cref="RestoreBackupAsync"/> was asked (tests).</summary>
    public List<RestoreRequest> Restores { get; } = [];

    private BackupState FakeBackupState()
    {
        var state = new BackupState
        {
            Schedule = _backupSchedule.Clone(),
            HasPassword = true,
            Folder = @"C:\ProgramData\OADM\backups",
            LastAutomatic = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 10, 13, 0, 0, 3, TimeSpan.Zero)),
        };
        state.Schedule.Password = "";
        state.Backups.AddRange(_serverBackups);
        return state;
    }

    public Task<BackupState> GetBackupStateAsync(CancellationToken ct) => Task.FromResult(FakeBackupState());

    public Task<BackupState> SetBackupScheduleAsync(BackupSchedule schedule, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (schedule.Keep is < 1 or > 365)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Keep 1 to 365 backups."));
        }

        _backupSchedule = schedule.Clone();
        return Task.FromResult(FakeBackupState());
    }

    public Task<Oadm.Sdk.Plugins.PluginDownload> CreateBackupAsync(string password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Use at least 8 characters."));
        }

        var bytes = Encoding.UTF8.GetBytes("OADMBACKUP fake backup");
        return Task.FromResult(new Oadm.Sdk.Plugins.PluginDownload(new MemoryStream(bytes), "oadm-backup-fake.oadmbackup", bytes.Length));
    }

    public Task<Oadm.Sdk.Plugins.PluginDownload> DownloadBackupAsync(string name, CancellationToken ct)
    {
        var backup = _serverBackups.FirstOrDefault(b => b.Name == name) ?? throw new RpcException(new Status(StatusCode.NotFound, "There is no such backup on the server."));
        var bytes = Encoding.UTF8.GetBytes("OADMBACKUP fake backup");
        return Task.FromResult(new Oadm.Sdk.Plugins.PluginDownload(new MemoryStream(bytes), backup.Name, bytes.Length));
    }

    public Task<BackupState> DeleteBackupAsync(string name, CancellationToken ct)
    {
        if (_serverBackups.RemoveAll(b => b.Name == name) == 0)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "There is no such backup on the server."));
        }

        return Task.FromResult(FakeBackupState());
    }

    public Task<BackupInfo> InspectBackupAsync(RestoreRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Password == "wrong")
        {
            return Task.FromResult(new BackupInfo { WrongPassword = true });
        }

        if (request.FileId.StartsWith("future", StringComparison.Ordinal))
        {
            return Task.FromResult(new BackupInfo { Problem = "This backup is from OADM 9.0.0, newer than this server (2.7.0). Update OADM to 9.0.0 or later first.", OadmVersion = "9.0.0" });
        }

        var info = new BackupInfo
        {
            OadmVersion = "2.6.0",
            ServerName = "site-a",
            Kind = "automatic",
            Created = Timestamp.FromDateTimeOffset(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero)),
        };
        info.Lines.AddRange(["Devices: 24", "Device logins: 24", "Credential list: 3", "Users: 4", "Roles: 4", "Tags: 4", "Event rules: 3", "Tasks in the history: 1,204", "Plugin data folders: 3"]);
        return Task.FromResult(info);
    }

    public Task<RestoreReply> RestoreBackupAsync(RestoreRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Password == "wrong")
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The password is wrong. Nothing was changed."));
        }

        Restores.Add(request);
        return Task.FromResult(new RestoreReply
        {
            Message = "The server restarts and restores the backup. Log in again in a minute.",
            SafetyBackup = "oadm-backup-20261013-140211-before-restore.oadmbackup",
        });
    }
}
