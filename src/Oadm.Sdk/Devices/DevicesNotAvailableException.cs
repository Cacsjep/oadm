namespace Oadm.Sdk.Devices;

/// <summary>
/// A request named devices the caller cannot reach: removed devices, unknown ids, or devices outside the user's device
/// group. The host answers NOT_FOUND with <see cref="DeviceMessages.NoLongerAvailable"/> and nothing of the request runs.
/// Thrown by the scoped device access of a user's call (<see cref="Plugins.ICallContext"/>) and by the host's device views.
/// </summary>
public sealed class DevicesNotAvailableException : Exception
{
    /// <summary>One device is not available.</summary>
    public DevicesNotAvailableException()
        : this(1)
    {
    }

    /// <summary><paramref name="count"/> devices are not available.</summary>
    public DevicesNotAvailableException(int count)
        : base(DeviceMessages.NoLongerAvailable(count))
    {
        Count = Math.Max(1, count);
    }

    /// <summary>With an own message (keep it free of device names and addresses the caller may not see).</summary>
    public DevicesNotAvailableException(string message)
        : base(message)
    {
        Count = 1;
    }

    /// <summary>With an own message and the cause.</summary>
    public DevicesNotAvailableException(string message, Exception innerException)
        : base(message, innerException)
    {
        Count = 1;
    }

    /// <summary>How many of the requested devices are not available.</summary>
    public int Count { get; }
}
