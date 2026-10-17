namespace Oadm.Server.Tests.Support;

/// <summary>
/// Tests that set or read process-wide environment variables (DOTNET_BUNDLE_EXTRACT_BASE_DIR, read at a service-mode
/// start): never in parallel with any other test.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironment
{
    public const string Name = "Process environment variables";
}
