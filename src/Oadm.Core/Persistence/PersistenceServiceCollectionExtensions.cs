using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Oadm.Core.Devices;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the database, repositories, credential encryption and settings as singletons.
    /// Call <see cref="DatabaseInitializer.InitializeAsync"/> once at startup before using them.
    /// </summary>
    public static IServiceCollection AddOadmPersistence(this IServiceCollection services, OadmPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(paths);

        services.AddSingleton(paths);
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContextFactory<OadmDbContext>(o => o.UseSqlite(paths.ConnectionString));

        services.AddSingleton(_ => CredentialProtector.FromKeyFile(paths.MasterKeyPath));
        services.AddSingleton<CredentialStore>();

        services.AddSingleton<DeviceChangeFeed>();
        services.AddSingleton<IDeviceChangeFeed>(sp => sp.GetRequiredService<DeviceChangeFeed>());
        services.AddSingleton<DeviceRepository>();
        services.AddSingleton<IDeviceRepository>(sp => sp.GetRequiredService<DeviceRepository>());

        services.AddSingleton<TaskRecordStore>();
        services.AddSingleton<ITaskRecordStore>(sp => sp.GetRequiredService<TaskRecordStore>());

        services.AddSingleton<ServerSettingsStore>();
        services.AddSingleton<IPluginSettingsFactory, PluginSettingsFactory>();

        services.AddSingleton<DatabaseInitializer>();
        return services;
    }
}
