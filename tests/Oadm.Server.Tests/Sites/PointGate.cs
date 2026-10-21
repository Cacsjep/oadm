using Oadm.Server.Sites;

namespace Oadm.Server.Tests.Sites;

/// <summary>Holds the manager at one named test point (<see cref="SiteManager.TestPoint"/>) until the test opens it.</summary>
internal sealed class PointGate
{
    private readonly string _name;
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private PointGate(string name) => _name = name;

    /// <summary>Completes when the manager reached the point.</summary>
    public Task Entered => _entered.Task;

    public static PointGate Hold(SiteManager manager, string name)
    {
        var gate = new PointGate(name);
        manager.TestPoint = gate.ReachedAsync;
        return gate;
    }

    public void Open() => _open.TrySetResult();

    private Task ReachedAsync(string name, CancellationToken ct)
    {
        if (name != _name)
        {
            return Task.CompletedTask;
        }

        _entered.TrySetResult();
        return _open.Task.WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None); // held whatever the call does: the test decides
    }
}
