using Grpc.Core;

using Oadm.Contracts.V1;
using Oadm.Sdk.Licensing;

namespace Oadm.Client.Api;

/// <summary>
/// Fake LicenseService so <c>--fake</c> shows the Settings page's License card: no license at first; a PRO key signed with
/// <see cref="SampleLicenseKeys"/> for <see cref="FakeServerId"/> is accepted (tests sign one with
/// <see cref="ProLicenseFile.Sign"/>).
/// </summary>
public sealed partial class FakeOadmApi
{
    private string? _proLicense;

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

    private static ProLicenseInfo CheckFakeProLicense(string license) =>
        ProLicenseFile.Check(license, [SampleLicenseKeys.PublicKey], FakeServerId, DateOnly.FromDateTime(DateTime.Now));

    private ProLicenseState FakeProLicenseState()
    {
        var info = _proLicense is null ? ProLicenseInfo.None : CheckFakeProLicense(_proLicense);
        var state = new ProLicenseState
        {
            ServerId = FakeServerId,
            Status = (Oadm.Contracts.V1.ProLicenseStatus)(int)info.Status,
            Reason = info.Reason,
            Customer = info.Customer ?? "",
            Plan = info.Plan ?? "",
            Expires = info.Expires is { } day ? LicenseFile.Day(day) : "",
            Trial = info.IsTrial,
        };
        if (info.MaxDevices is { } max)
        {
            state.MaxDevices = max;
        }

        return state;
    }
}
