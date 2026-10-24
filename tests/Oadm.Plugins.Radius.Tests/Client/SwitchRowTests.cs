using Oadm.Plugins.Radius.Api;
using Oadm.Plugins.Radius.Client;

namespace Oadm.Plugins.Radius.Tests.Client;

/// <summary>
/// A switch that needs attention (docs/spec/radius-server.md, decision 2026-10-25: no State column): a dot before its name,
/// red for an error, amber for no requests yet, the state as its tooltip; nothing for a switch that answered.
/// </summary>
public sealed class SwitchRowTests
{
    [Theory]
    [InlineData(SwitchStates.Answered, false, false, null)]
    [InlineData(SwitchStates.NoRequests, true, false, "No requests yet")]
    [InlineData(SwitchStates.WrongSecret, true, true, "Wrong secret")]
    [InlineData(SwitchStates.SiteOffline, true, true, "Site offline")]
    public void Only_a_switch_with_a_problem_gets_the_dot_with_its_state(string state, bool problem, bool error, string? tip)
    {
        var row = new SwitchRowViewModel(new SwitchRow { Name = "Core switch", State = state });
        Assert.Equal((problem, error, tip), (row.HasProblem, row.IsError, row.ProblemTip));
    }
}
