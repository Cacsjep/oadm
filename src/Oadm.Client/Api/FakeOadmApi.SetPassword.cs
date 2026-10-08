using Grpc.Core;

using Oadm.Contracts.V1;

namespace Oadm.Client.Api;

/// <summary>Fake mode of the device context menu's "Set password" (first password of factory-default devices), in memory.</summary>
public sealed partial class FakeOadmApi
{
    /// <summary>The policy the fake factory-default devices report (10.0.0.40 is Password not set).</summary>
    public const string FakePassphrasePolicy = "complex";

    /// <summary>Like the server: devices in Password not set take the password, others are refused without a change.</summary>
    public async Task<SetFirstPasswordReply> SetFirstPasswordAsync(IReadOnlyCollection<string> deviceIds, string password, CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct).ConfigureAwait(false);
        lock (_gate)
        {
            ThrowIfOffline();
            if (deviceIds.Count == 0 || password.Length is < 1 or > 64 || password.Any(c => c is < (char)0x20 or > (char)0x7E))
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Password must be 1-64 printable ASCII characters."));
            }

            var reply = new SetFirstPasswordReply();
            var byId = _devices.ToDictionary(d => d.Id);
            foreach (string id in deviceIds.Distinct())
            {
                var result = new SetFirstPasswordResult { DeviceId = id };
                if (!byId.TryGetValue(id, out Device? device))
                {
                    result.Message = "The device is no longer managed by OADM.";
                }
                else if (device.Status == DeviceStatus.Unreachable)
                {
                    result.Message = "Unreachable - No route to host";
                }
                else if (device.Status != DeviceStatus.PasswordNotSet)
                {
                    result.Message = "The device already has a password. Nothing was changed.";
                }
                else
                {
                    result.Ok = true;
                    device.HasCredentials = true;
                    device.Status = DeviceStatus.Ok;
                    PublishUpdated(device);
                }

                reply.Results.Add(result);
            }

            return reply;
        }
    }

    public Task<IReadOnlyDictionary<string, string>> GetPassphrasePoliciesAsync(IReadOnlyCollection<string> deviceIds, CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            IReadOnlyDictionary<string, string> policies = deviceIds.Distinct().ToDictionary(id => id, _ => FakePassphrasePolicy);
            return Task.FromResult(policies);
        }
    }
}
