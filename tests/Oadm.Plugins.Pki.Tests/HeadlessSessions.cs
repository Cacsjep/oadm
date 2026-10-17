namespace Oadm.Plugins.Pki.Tests;

/// <summary>
/// The headless tests share one Avalonia session (<see cref="HeadlessEntry.Session"/>: Avalonia sets up once per process)
/// and run one after another.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeadlessSessions
{
    public const string Name = "Avalonia headless";
}
