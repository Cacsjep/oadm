using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>Fake mode of the device context menu's "Trust new certificate" (devices with Certificate changed), in memory.</summary>
public sealed partial class FakeOadmApi
{
    /// <summary>The fingerprint every fake device with Certificate changed presents now (10.0.0.33).</summary>
    public const string FakeNewFingerprint = "3F9A61C2D7B04E8815A2C9D3E6F70B1A2C4D5E6F708192A3B4C5D6E7F8091A2B";

    private const string FakeOldFingerprint = "A1B2C3D4E5F60718293A4B5C6D7E8F90A1B2C3D4E5F60718293A4B5C6D7E8F90";

    public Task<IReadOnlyList<PresentedCertificate>> GetPresentedCertificatesAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            var byId = _devices.ToDictionary(d => d.Id);
            var now = DateTimeOffset.UtcNow;
            IReadOnlyList<PresentedCertificate> list = [.. deviceIds.Distinct().Select(id => !byId.TryGetValue(id, out Device? device)
                ? new PresentedCertificate { DeviceId = id, Message = Oadm.Sdk.Devices.DeviceMessages.Removed }
                : device.Status != DeviceStatus.CertificateChanged
                    ? new PresentedCertificate { DeviceId = id, Message = "The device does not show Certificate changed. Nothing was changed." }
                    : new PresentedCertificate
                    {
                        DeviceId = id,
                        PinnedFingerprint = FakeOldFingerprint,
                        Fingerprint = FakeNewFingerprint,
                        Subject = "CN=" + device.Serial.ToLowerInvariant(),
                        Issuer = "CN=" + device.Serial.ToLowerInvariant(),
                        NotBeforeUnixMs = now.AddDays(-1).ToUnixTimeMilliseconds(),
                        NotAfterUnixMs = now.AddYears(5).ToUnixTimeMilliseconds(),
                        Trust = CertificateTrust.SelfSigned,
                    })];
            return Task.FromResult(list);
        }
    }

    /// <summary>Like the server: the shown fingerprint is trusted on devices with Certificate changed, others are refused.</summary>
    public async Task<TrustNewCertificatesReply> TrustNewCertificatesAsync(IReadOnlyDictionary<string, string> fingerprints, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
        lock (_gate)
        {
            ThrowIfOffline();
            var reply = new TrustNewCertificatesReply();
            var byId = _devices.ToDictionary(d => d.Id);
            foreach ((string id, string fingerprint) in fingerprints)
            {
                var result = new TrustNewCertificateResult { DeviceId = id };
                if (!byId.TryGetValue(id, out Device? device))
                {
                    result.Message = Oadm.Sdk.Devices.DeviceMessages.Removed;
                }
                else if (device.Status != DeviceStatus.CertificateChanged)
                {
                    result.Message = "The device does not show Certificate changed. Nothing was changed.";
                }
                else if (fingerprint != FakeNewFingerprint)
                {
                    result.Message = "The device now presents another certificate than shown. Nothing was changed.";
                }
                else
                {
                    result.Ok = true;
                    device.Status = DeviceStatus.Ok;
                    PublishUpdated(device);
                }

                reply.Results.Add(result);
            }

            return reply;
        }
    }
}
