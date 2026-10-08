using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Core.Tests.Plugins;

/// <summary>SDK texts and host rules for <see cref="ITaskPlugin.NotSupportedReason"/>.</summary>
public sealed class TaskSupportReasonsTests
{
    [Fact]
    public void APluginWithoutAReasonGetsTheDefaultText()
    {
        var plugin = new ReasonPlugin(_ => null);
        Assert.Null(((ITaskPlugin)plugin).NotSupportedReason(Device()));
        Assert.Equal("Not supported on this device", TaskSupportReasons.Of(new DefaultPlugin(), Device(), out var error));
        Assert.Null(error);
        Assert.Equal(TaskSupportReasons.Default, TaskSupportReasons.Of(plugin, Device(), out _));
        Assert.Equal(TaskSupportReasons.Default, TaskSupportReasons.Of(new ReasonPlugin(_ => "   "), Device(), out _));
    }

    [Fact]
    public void AThrowingReasonIsTheDefaultTextAndHandsOutTheException()
    {
        var boom = new InvalidOperationException("broken");
        Assert.Equal(TaskSupportReasons.Default, TaskSupportReasons.Of(new ReasonPlugin(_ => throw boom), Device(), out var error));
        Assert.Same(boom, error);
    }

    [Fact]
    public void ReasonsAreTrimmedAndLongOnesShortened()
    {
        Assert.Equal("Needs X", TaskSupportReasons.Of(new ReasonPlugin(_ => "  Needs X "), Device(), out _));
        var text = TaskSupportReasons.Of(new ReasonPlugin(_ => new string('a', 400)), Device(), out _);
        Assert.Equal(TaskSupportReasons.MaxLength, text.Length);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpersWritePlainLanguageWithTheDeviceDetailAtTheEnd()
    {
        Assert.Equal("Needs AXIS OS 11.11 or later (this device has 11.9.65)", TaskSupportReasons.NeedsFirmware("11.11", Device("11.9.65")));
        Assert.Equal("Needs AXIS OS 11.11 or later", TaskSupportReasons.NeedsFirmware("11.11", Device(null)));
        Assert.Equal("Needs the Time API (this device has 9.80.3)", TaskSupportReasons.NeedsApi("the Time API", Device("9.80.3", [new DeviceApi("param-cgi", "1.0")])));
        Assert.Equal(TaskSupportReasons.ApisNotRead, TaskSupportReasons.NeedsApi("the Time API", Device("9.80.3", [])));

        Assert.Equal("Needs AXIS OS 11.11 or later", TaskSupportReasons.General("Needs AXIS OS 11.11 or later (this device has 11.9.65)"));
        Assert.Equal("Needs X (see the manual)", TaskSupportReasons.General("Needs X (see the manual)"));
        Assert.Equal(TaskSupportReasons.ApisNotRead, TaskSupportReasons.General(TaskSupportReasons.ApisNotRead));

        Assert.Null(TaskSupportReasons.ForStatus(DeviceStatus.Ok));
        Assert.Null(TaskSupportReasons.ForStatus(DeviceStatus.Unknown));
        Assert.NotNull(TaskSupportReasons.ForStatus(DeviceStatus.Unknown, requireOk: true));
        Assert.Equal("The device does not answer", TaskSupportReasons.ForStatus(DeviceStatus.Unreachable));
        Assert.Equal("OADM cannot log in to the device: use Log in", TaskSupportReasons.ForStatus(DeviceStatus.CredentialsRequired));
        Assert.Equal("The device has no password yet: use Set password", TaskSupportReasons.ForStatus(DeviceStatus.PasswordNotSet));
        Assert.Equal("The device's certificate changed since it was added", TaskSupportReasons.ForStatus(DeviceStatus.CertificateChanged));
    }

    private static TestDevice Device(string? firmware = "12.11.77", IReadOnlyList<DeviceApi>? apis = null) =>
        new(firmware, apis ?? [new DeviceApi("time-service", "1.0")]);

    private sealed record TestDevice(string? FirmwareVersion, IReadOnlyList<DeviceApi> Apis) : IDeviceInfo
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Serial => "B8A44F631339";
        public string Address => "10.0.0.48";
        public string? HostName => null;
        public string? Model => "P3265-V";
        public DeviceStatus Status => DeviceStatus.Ok;
        public DeviceCategory Category => DeviceCategory.Camera;
        public bool HasVideo => true;
    }

    private class DefaultPlugin : ITaskPlugin
    {
        public string Id => "test.reason";
        public string DisplayName => "Reason";
        public string? IconKey => null;
        public bool ShowInToolbar => false;
        public bool RequiresDialog => false;
        public bool CanRun(IDeviceInfo device) => false;
        public Task ExecuteAsync(ITaskExecutionContext ctx, IDeviceInfo device, string? payloadJson, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ReasonPlugin(Func<IDeviceInfo, string?> reason) : DefaultPlugin, ITaskPlugin
    {
        public string? NotSupportedReason(IDeviceInfo device) => reason(device);
    }
}
