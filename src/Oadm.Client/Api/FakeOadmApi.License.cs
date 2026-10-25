using Grpc.Core;

using Oadm.Contracts.V1;
using Oadm.Sdk.Licensing;

namespace Oadm.Client.Api;

/// <summary>
/// Fake LicenseService so <c>--fake</c> shows the Settings page's License card: no license at first; a PRO key signed with
/// <see cref="SampleLicenseKeys"/> for <see cref="FakeServerId"/> is accepted (tests sign one with
/// <see cref="ProLicenseFile.Sign"/>); Start 30-day trial runs once, like on a real server.
/// </summary>
public sealed partial class FakeOadmApi
{
    private string? _proLicense;
    private DateOnly? _proTrialStarted;

    public Task<ProLicenseState> GetLicenseAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            return Task.FromResult(FakeProLicenseState());
        }
    }

    public Task<ProLicenseState> ImportLicenseAsync(string license, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var info = CheckFakeProLicense(license);
            if (!info.IsActive)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, info.Reason));
            }

            _proLicense = license;
            return Task.FromResult(FakeProLicenseState());
        }
    }

    public Task<ProLicenseState> RemoveLicenseAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            _proLicense = null;
            return Task.FromResult(FakeProLicenseState());
        }
    }

    public Task<ProLicenseState> StartTrialAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            if (_proTrialStarted is not null)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, ProLicenseTexts.TrialUsed));
            }

            if (FakeProLicenseInfo().IsActive)
            {
                throw new RpcException(new Status(StatusCode.FailedPrecondition, ProLicenseTexts.AlreadyActive));
            }

            _proTrialStarted = FakeToday();
            return Task.FromResult(FakeProLicenseState());
        }
    }

    /// <summary>The fake license as the server's IProLicense would say it: an active key, else the trial.</summary>
    public ProLicenseInfo FakeProLicenseInfo()
    {
        lock (_gate)
        {
            var key = _proLicense is null ? null : CheckFakeProLicense(_proLicense);
            if (key is { IsActive: true })
            {
                return key;
            }

            ProLicenseInfo? trial = null;
            if (_proTrialStarted is { } started)
            {
                var ends = started.AddDays(ProLicenseTexts.TrialDays - 1);
                trial = ends >= FakeToday()
                    ? new ProLicenseInfo(Oadm.Sdk.Licensing.ProLicenseStatus.Active, ProLicenseTexts.Active(null, ends, trial: true), Expires: ends, IsTrial: true)
                    : new ProLicenseInfo(Oadm.Sdk.Licensing.ProLicenseStatus.Expired, ProLicenseTexts.TrialEndedOn(ends), Expires: ends, IsTrial: true);
                if (trial.IsActive)
                {
                    return trial;
                }
            }

            return (key ?? trial ?? ProLicenseInfo.None) with { TrialAvailable = trial is null };
        }
    }

    private static DateOnly FakeToday() => DateOnly.FromDateTime(DateTime.Now);

    private static ProLicenseInfo CheckFakeProLicense(string license) =>
        ProLicenseFile.Check(license, [SampleLicenseKeys.PublicKey], FakeServerId, DateOnly.FromDateTime(DateTime.Now));

    private ProLicenseState FakeProLicenseState()
    {
        var info = FakeProLicenseInfo();
        var state = new ProLicenseState
        {
            ServerId = FakeServerId,
            Status = (Oadm.Contracts.V1.ProLicenseStatus)(int)info.Status,
            Reason = info.Reason,
            Customer = info.Customer ?? "",
            Plan = info.Plan ?? "",
            Expires = info.Expires is { } day ? LicenseFile.Day(day) : "",
            Trial = info.IsTrial,
            TrialAvailable = info.TrialAvailable,
            HasKey = _proLicense is not null,
        };
        if (info.MaxDevices is { } max)
        {
            state.MaxDevices = max;
        }

        return state;
    }
}
