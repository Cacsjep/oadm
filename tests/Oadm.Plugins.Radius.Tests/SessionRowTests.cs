using Microsoft.Extensions.Time.Testing;

using Oadm.Plugins.Radius.Protocol.Sessions;
using Oadm.Plugins.Radius.Protocol.Text;
using Oadm.Plugins.Radius.Server;
using Oadm.Sdk.Devices;
using Oadm.Sdk.Testing;

namespace Oadm.Plugins.Radius.Tests;

/// <summary>
/// The device of a session row (docs/spec/radius-server.md "Device"): only the checked certificate decides which device a row
/// belongs to; a guess from the MAC address is shown, never stored as its device id.
/// </summary>
public sealed class SessionRowTests
{
    [Fact]
    public async Task A_guess_from_the_mac_address_is_shown_but_never_the_rows_device()
    {
        var camera = new FakeDevice { Serial = "ACCC8E123456", Address = "10.0.0.48" };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 25, 8, 0, 0, TimeSpan.Zero));
        using var index = new DeviceIndex(new Devices([camera]), time);
        await index.RefreshAsync(onlyAfterMiss: false, CancellationToken.None);
        var snapshot = new EapSessionSnapshot
        {
            Id = Guid.NewGuid(),
            Client = "switch-1",
            Started = time.GetUtcNow(),
            LastMessage = time.GetUtcNow(),
            State = EapSessionState.StartingTls,
            Mac = MacAddress.Parse("AC-CC-8E-12-34-56"),
        };

        var row = SessionBoard.RowOf(snapshot, "Core switch", index, time.GetUtcNow());

        Assert.True(row.DeviceGuess);
        Assert.Null(row.DeviceId); // who may see the row and its log entry: never from a MAC address (it can be faked)
        Assert.Equal("10.0.0.48", row.IpAddress);
    }

    private sealed class Devices(IReadOnlyList<IDeviceInfo> list) : IDeviceRepository
    {
        public Task<IReadOnlyList<IDeviceInfo>> ListAsync(CancellationToken ct) => Task.FromResult(list);

        public Task<IDeviceInfo?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(list.FirstOrDefault(d => d.Id == id));
    }
}
