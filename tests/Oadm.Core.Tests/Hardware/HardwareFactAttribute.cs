namespace Oadm.Core.Tests.Hardware;

/// <summary>
/// A fact that talks to real devices from dev-cameras.yaml. Skipped when no cameras are
/// configured (CI). Combine with <c>[Trait("Category", "Hardware")]</c> on the test class so the
/// tests can be filtered: <c>dotnet test --filter Category!=Hardware</c>.
/// Hardware tests must be read-only unless a later milestone explicitly allows writes.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class HardwareFactAttribute : FactAttribute
{
    public HardwareFactAttribute()
    {
        if (!DevCameras.Any)
        {
            Skip = $"No dev cameras configured ({DevCameras.FileName} not found and {DevCameras.EnvironmentVariable} not set).";
        }
    }
}
