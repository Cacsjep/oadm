namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// Tests that start an Avalonia headless session run one after another: two sessions at the same time in one test
/// process deadlock (the test host never exits).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeadlessSessions
{
    public const string Name = "Avalonia headless";
}
