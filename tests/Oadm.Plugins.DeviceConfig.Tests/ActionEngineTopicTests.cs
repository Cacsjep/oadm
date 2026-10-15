using Oadm.Plugins.DeviceConfig.Configure;

namespace Oadm.Plugins.DeviceConfig.Tests;

/// <summary>AXIS OS 10.12 reads condition topics with "//." appended and refuses them written back that way.</summary>
public sealed class ActionEngineTopicTests
{
    [Theory]
    [InlineData("tns1:UserAlarm/tnsaxis:Recurring/Interval//.", "tns1:UserAlarm/tnsaxis:Recurring/Interval")]
    [InlineData("tns1:Device/tnsaxis:IO/VirtualInput", "tns1:Device/tnsaxis:IO/VirtualInput")]
    public void Topics_are_written_without_the_marker(string read, string written) => Assert.Equal(written, ActionEngineApi.Topic(read));
}
