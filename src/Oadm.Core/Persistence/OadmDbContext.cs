using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Settings;

namespace Oadm.Core.Persistence;

public sealed class OadmDbContext(DbContextOptions<OadmDbContext> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceCredential> DeviceCredentials => Set<DeviceCredential>();
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();
    public DbSet<TaskDeviceResultEntity> TaskDeviceResults => Set<TaskDeviceResultEntity>();
    public DbSet<Setting> Settings => Set<Setting>();

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

    private static readonly ValueComparer<List<string>> TagsComparer =
        new(
            (a, b) => (a == null && b == null) || (a != null && b != null && a.SequenceEqual(b)),
            v => v.Aggregate(0, (hash, tag) => HashCode.Combine(hash, tag.GetHashCode(StringComparison.Ordinal))),
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
            e.Property(d => d.Category).HasConversion<string>().HasMaxLength(16);
            e.Ignore(d => d.HasVideo);
            e.Property(d => d.CertNotAfterUtc).HasConversion(NullableUtcConverter);
            e.Property(d => d.CertTrust).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.CertSubject).HasMaxLength(1024);
            e.Property(d => d.CertIssuer).HasMaxLength(1024);
            e.Property(d => d.Tags).HasConversion(TagsConverter, TagsComparer).IsRequired();
        });

        modelBuilder.Entity<DeviceCredential>(e =>
        {
            e.ToTable("DeviceCredentials");
            e.HasKey(c => c.DeviceId);
            e.Property(c => c.UserName).IsRequired().HasMaxLength(64);
            e.Property(c => c.EncryptedPassword).IsRequired();
            e.HasOne<Device>().WithOne().HasForeignKey<DeviceCredential>(c => c.DeviceId).OnDelete(DeleteBehavior.Cascade);
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
            e.HasMany(t => t.Results).WithOne().HasForeignKey(r => r.TaskId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskDeviceResultEntity>(e =>
        {
            e.ToTable("TaskDeviceResults");
            e.HasKey(r => new { r.TaskId, r.DeviceId });
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(r => r.DeviceId);
        });

        modelBuilder.Entity<Setting>(e =>
        {
            e.ToTable("Settings");
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(255);
            e.Property(s => s.ValueJson).IsRequired();
        });
    }
}
