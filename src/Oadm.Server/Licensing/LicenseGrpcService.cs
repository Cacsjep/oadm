using Grpc.Core;

using Oadm.Core.Auth;
using Oadm.Core.Licensing;
using Oadm.Sdk.Licensing;
using Oadm.Server.Common;

using Proto = Oadm.Contracts.V1;

namespace Oadm.Server.Licensing;

/// <summary>
/// gRPC LicenseService: the server's OADM PRO license (docs/spec/licensing.md). Every signed-in user reads it; Import,
/// Remove and Start trial are Administrator only and audited.
/// </summary>
public sealed class LicenseGrpcService(ProLicense license, AuditLog audit) : Proto.LicenseService.LicenseServiceBase
{
    public override Task<Proto.ProLicenseState> GetLicense(Proto.Empty request, ServerCallContext context) =>
        Task.FromResult(ToProto(license.Current, license));

    public override async Task<Proto.ProLicenseState> ImportLicense(Proto.ImportLicenseRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.License) || request.License.Length > LicenseFile.MaxLength)
        {
            throw GrpcGuard.InvalidArgument(ProLicenseTexts.NotValid);
        }

        ProLicenseInfo info;
        try
        {
            info = await license.ImportAsync(request.License, context.CancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            throw GrpcGuard.InvalidArgument(ex.Message);
        }

        await audit.WriteAsync(AuditActions.ProLicenseImported, "OADM PRO", $"{info.Customer}: {info.Reason}", CancellationToken.None).ConfigureAwait(false);
        return ToProto(info, license);
    }

    public override async Task<Proto.ProLicenseState> RemoveLicense(Proto.Empty request, ServerCallContext context)
    {
        var before = license.Current;
        var hadKey = license.HasKey;
        var info = await license.RemoveAsync(context.CancellationToken).ConfigureAwait(false);
        if (hadKey)
        {
            await audit.WriteAsync(AuditActions.ProLicenseRemoved, "OADM PRO", before.Customer ?? "", CancellationToken.None).ConfigureAwait(false);
        }

        return ToProto(info, license);
    }

    public override async Task<Proto.ProLicenseState> StartTrial(Proto.Empty request, ServerCallContext context)
    {
        ProLicenseInfo info;
        try
        {
            info = await license.StartTrialAsync(context.CancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw GrpcGuard.FailedPrecondition(ex.Message);
        }

        await audit.WriteAsync(AuditActions.ProTrialStarted, "OADM PRO", info.Reason, CancellationToken.None).ConfigureAwait(false);
        return ToProto(info, license);
    }

    /// <summary>The wire form of the current state of <paramref name="license"/>.</summary>
    public static Proto.ProLicenseState ToProto(ProLicenseInfo info, ProLicense license)
    {
        ArgumentNullException.ThrowIfNull(license);
        var state = ToProto(info, license.ServerId);
        state.HasKey = license.HasKey;
        return state;
    }

    /// <summary>The wire form of a checked license.</summary>
    public static Proto.ProLicenseState ToProto(ProLicenseInfo info, string serverId)
    {
        ArgumentNullException.ThrowIfNull(info);
        var state = new Proto.ProLicenseState
        {
            ServerId = serverId ?? "",
            Status = (Proto.ProLicenseStatus)(int)info.Status,
            Reason = info.Reason,
            Customer = info.Customer ?? "",
            Plan = info.Plan ?? "",
            Expires = info.Expires is { } day ? LicenseFile.Day(day) : "",
            Trial = info.IsTrial,
            TrialAvailable = info.TrialAvailable,
        };
        if (info.MaxDevices is { } max)
        {
            state.MaxDevices = max;
        }

        return state;
    }
}
