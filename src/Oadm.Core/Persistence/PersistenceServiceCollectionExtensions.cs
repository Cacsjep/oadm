using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Oadm.Core.Auth;
using Oadm.Core.Devices;
using Oadm.Core.Plugins;
using Oadm.Core.Security;
using Oadm.Core.Settings;
using Oadm.Core.Tasks;
using Oadm.Sdk.Devices;

namespace Oadm.Core.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the database, repositories, credential encryption, settings, the EF task store
    /// (<see cref="ITaskStore"/>) and plugin settings (<see cref="IPluginSettingsProvider"/>) as singletons.
    /// Call <see cref="DatabaseInitializer.InitializeAsync"/> once at startup before using them.
    /// </summary>
    public static IServiceCollection AddOadmPersistence(this IServiceCollection services, OadmPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return services.AddOadmPersistence(_ => paths);
    }

    /// <summary>
    /// Same as <see cref="AddOadmPersistence(IServiceCollection, OadmPaths)"/>, with the data folder
    /// resolved lazily from the container (e.g. from configuration that is only final after build).
    /// </summary>
    public static IServiceCollection AddOadmPersistence(this IServiceCollection services, Func<IServiceProvider, OadmPaths> pathsFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(pathsFactory);

        services.AddSingleton(pathsFactory);
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContextFactory<OadmDbContext>((sp, o) => o
            .UseSqlite(sp.GetRequiredService<OadmPaths>().ConnectionString)
            .AddInterceptors(SqlitePragmaInterceptor.Instance));

        services.AddSingleton(sp => CredentialProtector.FromKeyFile(sp.GetRequiredService<OadmPaths>().MasterKeyPath));
        services.AddSingleton<CredentialStore>();
        services.AddSingleton<CredentialListStore>();

        services.AddSingleton<DeviceChangeFeed>();
        services.AddSingleton<IDeviceChangeFeed>(sp => sp.GetRequiredService<DeviceChangeFeed>());
        services.AddSingleton<DeviceRepository>();
        services.AddSingleton<IDeviceRepository>(sp => sp.GetRequiredService<DeviceRepository>());

        services.AddSingleton<EfTaskStore>();
        services.AddSingleton<ITaskStore>(sp => sp.GetRequiredService<EfTaskStore>());

        services.AddSingleton<ServerSettingsStore>();
        services.AddSingleton<IPluginSettingsProvider, DbPluginSettingsProvider>();

        // Users, sessions and the audit log (Production hardening: access)
        services.TryAddSingleton(_ => new PasswordHasher());
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<AuthTokenStore>();
        services.AddSingleton<UserStore>();
        services.AddSingleton<AuditLog>();
        services.AddSingleton<AuthManager>();

        services.AddSingleton<DatabaseInitializer>();
        return services;
    }
}
