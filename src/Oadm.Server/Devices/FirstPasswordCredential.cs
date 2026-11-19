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
            ? await ReadAsync(list, id, context.CancellationToken).ConfigureAwait(false)
            : FirstPasswordEntry.Gone;
        return entry.Problem switch
        {
            FirstPasswordProblem.None => entry.Password!,
            FirstPasswordProblem.Gone => throw GrpcGuard.NotFound(entry.Message!),
            _ => throw GrpcGuard.InvalidArgument(entry.Message!),
        };
    }

    /// <summary>
    /// The one check of a first password from the credential list (the gRPC services above and the core plugins'
    /// <c>IDeviceAutoAdd.SetFirstPasswordAsync</c>): the entry exists, is root's, and its password fits a first password.
    /// The caller checks who may use the list.
    /// </summary>
    public static async Task<FirstPasswordEntry> ReadAsync(CredentialListStore list, Guid credentialId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (await list.RevealAsync(credentialId, ct).ConfigureAwait(false) is not { } entry)
        {
            return FirstPasswordEntry.Gone;
        }

        if (!string.Equals(entry.UserName, "root", StringComparison.Ordinal))
        {
            return new FirstPasswordEntry(null, FirstPasswordProblem.NotRoot, DeviceFirstSetupTexts.RootOnly);
        }

        try
        {
            VapixClient.ValidatePassword(entry.Password);
        }
        catch (ArgumentException)
        {
            return new FirstPasswordEntry(null, FirstPasswordProblem.DoesNotFit, DoesNotFit);
        }

        return new FirstPasswordEntry(entry.Password, FirstPasswordProblem.None, null);
    }
}

/// <summary>Why an entry of the credential list cannot be a first password.</summary>
internal enum FirstPasswordProblem
{
    None,

    /// <summary>The entry is no longer in the credential list.</summary>
    Gone,

    /// <summary>The entry is another user's than root's.</summary>
    NotRoot,

    /// <summary>The stored password does not fit a first password.</summary>
    DoesNotFit,
}

/// <summary>The stored password of a credential list entry checked as a first password, or the problem and its text.</summary>
internal sealed record FirstPasswordEntry(string? Password, FirstPasswordProblem Problem, string? Message)
{
    public static FirstPasswordEntry Gone { get; } = new(null, FirstPasswordProblem.Gone, DeviceFirstSetupTexts.CredentialGone);
}
