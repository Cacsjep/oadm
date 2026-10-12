using System.Security.Cryptography;

using Oadm.Core.Auth;
using Oadm.Sdk.Plugins;

namespace Oadm.Server.Auth;

/// <summary>
/// Session tokens for code that hosts the server in its own process (tests, hardware tests through the in-process
/// server): creates the user with a random password when it does not exist and returns a token for it, without a
/// login over gRPC.
/// </summary>
public static class InProcessAccess
{
    public static Task<string> CreateTokenAsync(IServiceProvider services, string userName = "admin", UserRole role = UserRole.Admin, CancellationToken ct = default) =>
        CreateTokenAsync(services, userName, BuiltInRoles.IdOf(role), ct);

    /// <summary>Like <see cref="CreateTokenAsync(IServiceProvider, string, UserRole, CancellationToken)"/> with any role.</summary>
    public static async Task<string> CreateTokenAsync(IServiceProvider services, string userName, Guid roleId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var users = services.GetRequiredService<UserStore>();
        var user = await users.FindByNameAsync(userName, ct).ConfigureAwait(false)
            ?? await users.CreateAsync(userName, Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)), roleId, onlyIfNoUsers: false, ct).ConfigureAwait(false);
        return await services.GetRequiredService<AuthTokenStore>().CreateAsync(user, remember: false, "in-process", ct).ConfigureAwait(false);
    }
}
