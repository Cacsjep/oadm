using Grpc.Core;

using Oadm.Core.Security;
using Oadm.Core.Vapix;
using Oadm.Sdk.Devices;
using Oadm.Server.Auth;
using Oadm.Server.Common;

namespace Oadm.Server.Devices;

/// <summary>
/// The first root password from an entry of the credential list (the credential choice of the add page's first password
/// editor and of the context menu's Set password): the server reads the stored password itself, so it never reaches the
/// client. The credential list's rule applies (Administrator only); the entry must be root's and its password must fit a
/// first password.
/// </summary>
internal static class FirstPasswordCredential
{
    /// <summary>A caller without the credential list (only an Administrator uses it).</summary>
    public const string AdminOnly = "Only an Administrator can use the credential list. Nothing was changed.";

    /// <summary>The stored password does not fit a first password (the rule of <see cref="VapixClient.ValidatePassword"/>).</summary>
    public const string DoesNotFit = "This password of the credential list cannot be a first password: it must have 1 to 64 characters and no accented letters. Nothing was changed.";

    /// <summary>A request with a typed password and a credential at once.</summary>
    public const string Both = "Type a password or choose one of the credential list, not both.";

    /// <summary>The stored password of the entry <paramref name="credentialId"/>, checked as a first password.</summary>
    /// <exception cref="RpcException">PERMISSION_DENIED not an Administrator, NOT_FOUND unknown entry, INVALID_ARGUMENT another
    /// user than root or a password that does not fit.</exception>
    public static async Task<string> PasswordAsync(CredentialListStore? list, string credentialId, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!AuthInterceptor.CallerOf(context).IsAdmin)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, AdminOnly));
        }

        if (list is null)
        {
            throw GrpcGuard.FailedPrecondition(DeviceFirstSetupTexts.NotSupported);
        }

        var entry = Guid.TryParse(credentialId, out var id)
            ? await list.RevealAsync(id, context.CancellationToken).ConfigureAwait(false)
            : null;
        if (entry is null)
        {
            throw GrpcGuard.NotFound(DeviceFirstSetupTexts.CredentialGone);
        }

        if (!string.Equals(entry.UserName, "root", StringComparison.Ordinal))
        {
            throw GrpcGuard.InvalidArgument(DeviceFirstSetupTexts.RootOnly);
        }

        try
        {
            VapixClient.ValidatePassword(entry.Password);
        }
        catch (ArgumentException)
        {
            throw GrpcGuard.InvalidArgument(DoesNotFit);
        }

        return entry.Password;
    }
}
