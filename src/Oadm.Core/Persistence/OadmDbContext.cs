using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Oadm.Core.Auth;
using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Persistence;

public sealed class OadmDbContext(DbContextOptions<OadmDbContext> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceCredential> DeviceCredentials => Set<DeviceCredential>();
    public DbSet<CredentialListEntry> CredentialListEntries => Set<CredentialListEntry>();
    public DbSet<WaitingDevice> WaitingDevices => Set<WaitingDevice>();
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
    public DbSet<TaskDeviceResultEntity> TaskDeviceResults => Set<TaskDeviceResultEntity>();
    public DbSet<TaskLogEntryEntity> TaskLogEntries => Set<TaskLogEntryEntity>();
    public DbSet<TaskStepEntity> TaskSteps => Set<TaskStepEntity>();

    /// <summary>Longer task log messages are cut to this length.</summary>
    public const int TaskLogEntryMaxLength = 2000;
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<Plugins.PluginRecord> PluginRecords => Set<Plugins.PluginRecord>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<RoleEntity> Roles => Set<RoleEntity>();
    public DbSet<AuthTokenEntity> AuthTokens => Set<AuthTokenEntity>();
    public DbSet<AuditEntryEntity> AuditEntries => Set<AuditEntryEntity>();
    public DbSet<TagDefinition> TagDefinitions => Set<TagDefinition>();
    public DbSet<Sites.Site> Sites => Set<Sites.Site>();
    public DbSet<Sites.RemovedSite> RemovedSites => Set<Sites.RemovedSite>();
    public DbSet<Events.EventRuleEntity> EventRules => Set<Events.EventRuleEntity>();
    public DbSet<Events.EventRuleStateEntity> EventRuleStates => Set<Events.EventRuleStateEntity>();
    public DbSet<Events.EventLogEntryEntity> EventLogEntries => Set<Events.EventLogEntryEntity>();
    public DbSet<Compliance.ComplianceCheckEntity> ComplianceChecks => Set<Compliance.ComplianceCheckEntity>();
    public DbSet<Compliance.ComplianceResultEntity> ComplianceResults => Set<Compliance.ComplianceResultEntity>();
    public DbSet<DeviceGroup> DeviceGroups => Set<DeviceGroup>();

    private static readonly ValueConverter<DateTime, DateTime> UtcConverter =
        new(v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> NullableUtcConverter =
        new(
            v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : v.Value.ToUniversalTime()) : v,
            v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    private static readonly ValueConverter<List<string>, string> TagsConverter =
        new(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());

    private static readonly ValueConverter<List<Guid>, string> GuidsConverter =
        new(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<Guid>>(v, (JsonSerializerOptions?)null) ?? new List<Guid>());

    private static readonly ValueComparer<List<Guid>> GuidsComparer =
        new(
            (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
            v => v.Aggregate(0, (hash, id) => HashCode.Combine(hash, id.GetHashCode())),
            v => v.ToList());

    private static readonly ValueComparer<List<string>> TagsComparer =
        new(
            (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
            v => v.Aggregate(0, (hash, tag) => HashCode.Combine(hash, tag.GetHashCode(StringComparison.Ordinal))),
            v => v.ToList());

    private static readonly ValueConverter<IReadOnlyList<DeviceApi>, string> ApisConverter =
        new(v => DeviceApiJson.Serialize(v), v => DeviceApiJson.DeserializeShared(v));

    private static readonly ValueComparer<IReadOnlyList<DeviceApi>> ApisComparer =
        new(
            (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
            v => v.Aggregate(0, (hash, api) => HashCode.Combine(hash, api.GetHashCode())),
            v => v.ToList());

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Device>(e =>
        {
            e.ToTable("Devices");
            e.HasKey(d => d.Id);
            e.HasIndex(d => d.Serial).IsUnique();
            e.Property(d => d.Serial).IsRequired().HasMaxLength(32);
            e.Property(d => d.Address).IsRequired().HasMaxLength(255);
            e.Property(d => d.HostName).HasMaxLength(255);
            e.Property(d => d.Model).HasMaxLength(128);
            e.Property(d => d.FirmwareVersion).HasMaxLength(64);
            e.Property(d => d.UpnpFriendlyName).HasMaxLength(255);
            e.Property(d => d.ServerName).HasMaxLength(255);
            e.Property(d => d.ReplacementModel).HasMaxLength(128);
            e.Property(d => d.CertFingerprintSha256).HasMaxLength(128);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(d => d.Scheme).HasConversion<string>().HasMaxLength(8);
            e.Property(d => d.LastSeenUtc).HasConversion(NullableUtcConverter);
            e.Property(d => d.ProductType).HasMaxLength(128);
            e.Property(d => d.Soc).HasMaxLength(64);
            e.Property(d => d.Architecture).HasMaxLength(32);
            e.Property(d => d.Category).HasConversion<string>().HasMaxLength(16);
            e.Ignore(d => d.HasVideo);
            e.Ignore(d => d.CredentialUserName);
            e.Property(d => d.CertNotAfterUtc).HasConversion(NullableUtcConverter);
            e.Property(d => d.CertTrust).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.CertSubject).HasMaxLength(1024);
            e.Property(d => d.CertIssuer).HasMaxLength(1024);
            e.Property(d => d.NtpState).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.Tags).HasConversion(TagsConverter, TagsComparer).IsRequired();
            e.Property(d => d.Apis).HasConversion(ApisConverter, ApisComparer).IsRequired();
            e.Ignore(d => d.SiteName);
            e.Ignore(d => d.StoredStatus);
            e.Ignore(d => d.SiteRemoved);
            e.HasIndex(d => d.SiteId);
            // Not a foreign key: a deleted group leaves the serial rule dormant (docs/spec/device-scope.md, Membership).
        });

        modelBuilder.Entity<DeviceCredential>(e =>
        {
            e.ToTable("DeviceCredentials");
            e.HasKey(c => c.DeviceId);
            e.Property(c => c.UserName).IsRequired().HasMaxLength(64);
            e.Property(c => c.EncryptedPassword).IsRequired();
            e.HasOne<Device>().WithOne().HasForeignKey<DeviceCredential>(c => c.DeviceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WaitingDevice>(e =>
        {
            e.ToTable("WaitingDevices");
            e.HasKey(w => w.Id);
            e.Property(w => w.Id).ValueGeneratedNever();
            e.Property(w => w.Address).IsRequired().HasMaxLength(WaitingDeviceStore.MaxAddressLength);
            e.Property(w => w.NormalizedAddress).IsRequired().HasMaxLength(WaitingDeviceStore.MaxAddressLength);
            e.HasIndex(w => w.NormalizedAddress).IsUnique();
            e.Property(w => w.ExpectedSerial).HasMaxLength(12);
            e.Property(w => w.UserName).HasMaxLength(CredentialListStore.MaxUserNameLength);
            e.Property(w => w.Tags).HasConversion(TagsConverter, TagsComparer).IsRequired();
            e.Property(w => w.Source).IsRequired().HasMaxLength(WaitingDeviceStore.MaxSourceLength);
            e.Property(w => w.AddedBy).IsRequired().HasMaxLength(64);
            e.Property(w => w.LastResult).IsRequired().HasMaxLength(WaitingDeviceStore.MaxResultLength);
            e.Property(w => w.CreatedUtc).HasConversion(UtcConverter);
            e.Property(w => w.LastTryUtc).HasConversion(NullableUtcConverter);
        });

        modelBuilder.Entity<CredentialListEntry>(e =>
        {
            e.ToTable("CredentialListEntries");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).ValueGeneratedNever();
            e.Property(c => c.UserName).IsRequired().HasMaxLength(CredentialListStore.MaxUserNameLength);
            e.Property(c => c.EncryptedPassword).IsRequired();
            e.Property(c => c.CreatedUtc).HasConversion(UtcConverter);
        });

        modelBuilder.Entity<TaskEntity>(e =>
        {
            e.ToTable("Tasks");
            e.HasKey(t => t.Id);
            e.Property(t => t.PluginId).IsRequired().HasMaxLength(128);
            e.Property(t => t.Name).IsRequired().HasMaxLength(255);
            e.Property(t => t.Owner).IsRequired().HasMaxLength(255);
            e.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(t => t.CreatedUtc).HasConversion(UtcConverter);
            e.Property(t => t.StartedUtc).HasConversion(NullableUtcConverter);
            e.Property(t => t.FinishedUtc).HasConversion(NullableUtcConverter);
            e.Property(t => t.ScheduledUtc).HasConversion(NullableUtcConverter);
            e.HasIndex(t => t.CreatedUtc);
            e.HasIndex(t => t.BatchId);
            // Active tasks (Queued, Running) are read on startup and by every Watch snapshot.
            e.HasIndex(t => t.Status);
            e.HasMany(t => t.Results).WithOne().HasForeignKey(r => r.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(t => t.Steps).WithOne().HasForeignKey(s => s.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskStepEntity>(e =>
        {
            e.ToTable("TaskSteps");
            e.HasKey(s => new { s.TaskId, s.Index });
            e.Property(s => s.Index).ValueGeneratedNever();
            e.Property(s => s.Name).IsRequired().HasMaxLength(Sdk.Plugins.TaskStepList.MaxNameLength);
            e.Property(s => s.State).HasConversion<string>().HasMaxLength(16);
            e.Property(s => s.Detail).HasMaxLength(Sdk.Plugins.TaskStepList.MaxDetailLength);
            e.Property(s => s.StartedUtc).HasConversion(NullableUtcConverter);
            e.Property(s => s.FinishedUtc).HasConversion(NullableUtcConverter);
        });

        modelBuilder.Entity<TaskDeviceResultEntity>(e =>
        {
            e.ToTable("TaskDeviceResults");
            e.HasKey(r => new { r.TaskId, r.DeviceId });
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(r => r.DeviceId);
        });

        modelBuilder.Entity<TaskLogEntryEntity>(e =>
        {
            e.ToTable("TaskLogEntries");
            e.HasKey(l => l.Id);
            e.Property(l => l.Id).ValueGeneratedOnAdd();
            e.Property(l => l.TimeUtc).HasConversion(UtcConverter);
            e.Property(l => l.Level).HasConversion<string>().HasMaxLength(16);
            e.Property(l => l.Message).IsRequired().HasMaxLength(TaskLogEntryMaxLength);
            e.HasIndex(l => l.TaskId);
            e.HasOne<TaskEntity>().WithMany().HasForeignKey(l => l.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserEntity>(e =>
        {
            e.ToTable("Users");
            e.HasKey(u => u.Id);
            e.Property(u => u.Id).ValueGeneratedNever();
            e.Property(u => u.UserName).IsRequired().HasMaxLength(UserStore.MaxUserNameLength);
            e.Property(u => u.NormalizedName).IsRequired().HasMaxLength(UserStore.MaxUserNameLength);
            e.HasIndex(u => u.NormalizedName).IsUnique();
            e.Property(u => u.PasswordHash).IsRequired().HasMaxLength(256);
            e.Ignore(u => u.Role);
            e.HasIndex(u => u.RoleId);
            e.HasOne<RoleEntity>().WithMany().HasForeignKey(u => u.RoleId).OnDelete(DeleteBehavior.Restrict);
            e.Ignore(u => u.DeviceAccess);
            e.HasIndex(u => u.DeviceGroupId);
            e.HasOne<DeviceGroup>().WithMany().HasForeignKey(u => u.DeviceGroupId).OnDelete(DeleteBehavior.Restrict);
            e.Property(u => u.CreatedUtc).HasConversion(UtcConverter);
            e.Property(u => u.LastLoginUtc).HasConversion(NullableUtcConverter);
        });

        modelBuilder.Entity<RoleEntity>(e =>
        {
            e.ToTable("Roles");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Name).IsRequired().HasMaxLength(RoleStore.MaxNameLength);
            e.Property(r => r.NormalizedName).IsRequired().HasMaxLength(RoleStore.MaxNameLength);
            e.HasIndex(r => r.NormalizedName).IsUnique();
            e.Property(r => r.Description).IsRequired().HasMaxLength(RoleStore.MaxDescriptionLength);
            e.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.PermissionsJson).IsRequired();
            e.Property(r => r.KnownJson).IsRequired();
            e.Property(r => r.StartPage).IsRequired().HasMaxLength(RoleStore.MaxStartPageLength);
            e.Property(r => r.CreatedUtc).HasConversion(UtcConverter);
            e.Property(r => r.ChangedUtc).HasConversion(UtcConverter);
        });

        modelBuilder.Entity<AuthTokenEntity>(e =>
        {
            e.ToTable("AuthTokens");
            e.HasKey(t => t.TokenHash);
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.Property(t => t.ClientAddress).IsRequired().HasMaxLength(64);
            e.Property(t => t.CreatedUtc).HasConversion(UtcConverter);
            e.Property(t => t.ExpiresUtc).HasConversion(UtcConverter);
            e.HasIndex(t => t.UserId);
            e.HasOne<UserEntity>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditEntryEntity>(e =>
        {
            e.ToTable("AuditEntries");
            e.HasKey(a => a.Id);
            e.Property(a => a.Id).ValueGeneratedOnAdd();
            e.Property(a => a.TimeUtc).HasConversion(UtcConverter);
            e.Property(a => a.UserName).IsRequired().HasMaxLength(AuditLog.MaxTextLength);
            e.Property(a => a.ClientAddress).IsRequired().HasMaxLength(AuditLog.MaxTextLength);
            e.Property(a => a.Action).IsRequired().HasMaxLength(AuditLog.MaxTextLength);
            e.Property(a => a.Target).IsRequired().HasMaxLength(AuditLog.MaxTextLength);
            e.Property(a => a.Detail).IsRequired().HasMaxLength(AuditLog.MaxTextLength);
            e.HasIndex(a => a.TimeUtc);
        });

        modelBuilder.Entity<Sites.Site>(e =>
        {
            e.ToTable("Sites");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).ValueGeneratedNever();
            e.Property(t => t.Name).IsRequired().HasMaxLength(Oadm.Core.Sites.Site.MaxNameLength);
            e.Property(t => t.NormalizedName).IsRequired().HasMaxLength(Oadm.Core.Sites.Site.MaxNameLength);
            e.HasIndex(t => t.NormalizedName).IsUnique();
            e.Property(t => t.Mode).HasConversion<string>().HasMaxLength(16);
            e.Property(t => t.Address).HasMaxLength(255);
            e.Property(t => t.NodeKeyFingerprint).IsRequired().HasMaxLength(64);
            e.HasIndex(t => t.NodeKeyFingerprint).IsUnique();
            e.Property(t => t.NodeId).IsRequired().HasMaxLength(64);
            e.HasIndex(t => t.NodeId).IsUnique();
            e.Property(t => t.Version).HasMaxLength(32);
            e.Property(t => t.BoundUtc).HasConversion(UtcConverter);
            e.Property(t => t.LastSeenUtc).HasConversion(NullableUtcConverter);
        });

        modelBuilder.Entity<Sites.RemovedSite>(e =>
        {
            e.ToTable("RemovedSites");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).ValueGeneratedNever();
            e.Property(t => t.Name).IsRequired().HasMaxLength(Oadm.Core.Sites.Site.MaxNameLength);
            e.Property(t => t.RemovedUtc).HasConversion(UtcConverter);
        });

        modelBuilder.Entity<TagDefinition>(e =>
        {
            e.ToTable("TagDefinitions");
            e.HasKey(t => t.Id);
            e.Property(t => t.Id).ValueGeneratedNever();
            e.Property(t => t.Name).IsRequired().HasMaxLength(TagNames.MaxLength);
            e.Property(t => t.NormalizedName).IsRequired().HasMaxLength(TagNames.MaxLength);
            e.HasIndex(t => t.NormalizedName).IsUnique();
            e.Property(t => t.Color).HasConversion<string>().HasMaxLength(16);
            e.Property(t => t.CreatedUtc).HasConversion(UtcConverter);
        });

        modelBuilder.Entity<Events.EventRuleEntity>(e =>
        {
            e.ToTable("EventRules");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).ValueGeneratedNever();
            e.Property(r => r.Name).IsRequired().HasMaxLength(Sdk.Events.RuleDefinition.MaxNameLength);
            e.Property(r => r.DefinitionJson).IsRequired();
            e.Property(r => r.CreatedUtc).HasConversion(UtcConverter);
            e.Property(r => r.UpdatedUtc).HasConversion(UtcConverter);
            e.Property(r => r.LastFiredUtc).HasConversion(NullableUtcConverter);
        });

        modelBuilder.Entity<Events.EventRuleStateEntity>(e =>
        {
            e.ToTable("EventRuleStates");
            e.HasKey(r => new { r.RuleId, r.SubjectId });
            e.Property(r => r.SinceUtc).HasConversion(UtcConverter);
            e.HasOne<Events.EventRuleEntity>().WithMany().HasForeignKey(r => r.RuleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Events.EventLogEntryEntity>(e =>
        {
            e.ToTable("EventLogEntries");
            e.HasKey(l => l.Id);
            e.Property(l => l.Id).ValueGeneratedOnAdd();
            e.Property(l => l.TimeUtc).HasConversion(UtcConverter);
            e.Property(l => l.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(l => l.Result).HasConversion<string>().HasMaxLength(16);
            e.Property(l => l.What).IsRequired().HasMaxLength(Events.EventLogEntryEntity.MaxTextLength);
            e.Property(l => l.Device).IsRequired().HasMaxLength(Events.EventLogEntryEntity.MaxTextLength);
            e.Property(l => l.Rule).IsRequired().HasMaxLength(Events.EventLogEntryEntity.MaxTextLength);
            e.Property(l => l.Message).IsRequired().HasMaxLength(Events.EventLogEntryEntity.MaxMessageLength);
            e.HasIndex(l => l.TimeUtc);
            e.HasIndex(l => l.RuleId);
        });

        modelBuilder.Entity<Compliance.ComplianceCheckEntity>(e =>
        {
            e.ToTable("ComplianceChecks");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).ValueGeneratedNever();
            e.Property(c => c.Name).IsRequired().HasMaxLength(Compliance.ComplianceStore.MaxNameLength);
            e.Property(c => c.TypeId).IsRequired().HasMaxLength(200);
            e.Property(c => c.SettingsJson).IsRequired();
            e.Property(c => c.DevicesJson).IsRequired();
            e.Property(c => c.CreatedUtc).HasConversion(UtcConverter);
            e.Property(c => c.ChangedUtc).HasConversion(UtcConverter);
            e.Ignore(c => c.Owner);
            e.HasIndex(c => c.OwnerGroupId);
            e.HasOne<DeviceGroup>().WithMany().HasForeignKey(c => c.OwnerGroupId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Compliance.ComplianceResultEntity>(e =>
        {
            e.ToTable("ComplianceResults");
            e.HasKey(r => new { r.CheckId, r.DeviceId });
            e.Property(r => r.Detail).IsRequired().HasMaxLength(Compliance.ComplianceResultEntity.MaxDetailLength);
            e.Property(r => r.CheckedUtc).HasConversion(UtcConverter);
            e.Property(r => r.SinceUtc).HasConversion(UtcConverter);
            e.HasOne<Compliance.ComplianceCheckEntity>().WithMany().HasForeignKey(r => r.CheckId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(r => r.DeviceId);
        });

        modelBuilder.Entity<DeviceGroup>(e =>
        {
            e.ToTable("DeviceGroups");
            e.HasKey(g => g.Id);
            e.Property(g => g.Id).ValueGeneratedNever();
            e.Property(g => g.Name).IsRequired().HasMaxLength(DeviceGroup.MaxNameLength);
            e.Property(g => g.NormalizedName).IsRequired().HasMaxLength(DeviceGroup.MaxNameLength);
            e.HasIndex(g => g.NormalizedName).IsUnique();
            e.Property(g => g.Description).IsRequired().HasMaxLength(DeviceGroup.MaxDescriptionLength);
            e.Property(g => g.SiteIds).HasConversion(GuidsConverter, GuidsComparer).IsRequired();
            e.Property(g => g.TagIds).HasConversion(GuidsConverter, GuidsComparer).IsRequired();
            e.Property(g => g.Serials).HasConversion(TagsConverter, TagsComparer).IsRequired();
            e.Property(g => g.ExceptSerials).HasConversion(TagsConverter, TagsComparer).IsRequired();
            e.Property(g => g.Revision).IsConcurrencyToken();
            e.Property(g => g.CreatedUtc).HasConversion(UtcConverter);
            e.Property(g => g.ChangedUtc).HasConversion(UtcConverter);
            e.Ignore(g => g.Rules);
        });

        modelBuilder.Entity<Setting>(e =>
        {
            e.ToTable("Settings");
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(255);
            e.Property(s => s.ValueJson).IsRequired();
        });

        modelBuilder.Entity<Plugins.PluginRecord>(e =>
        {
            e.ToTable("PluginRecords");
            e.HasKey(r => new { r.PluginId, r.Collection, r.Key });
            e.Property(r => r.PluginId).HasMaxLength(128);
            e.Property(r => r.Collection).HasMaxLength(Sdk.Plugins.PluginRecordRules.MaxCollectionLength);
            e.Property(r => r.Key).HasMaxLength(Sdk.Plugins.PluginRecordRules.MaxKeyLength);
            e.Property(r => r.ValueJson).IsRequired();
        });
    }
}
