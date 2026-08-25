using UpperHost.StateMachines;

namespace UpperHost.Tests;

public sealed class StateMachineTests
{
    private enum State { Offline, Online, Busy }
    private enum Trigger { Connect, Start }

    [Fact]
    public async Task State_machine_executes_configured_transition()
    {
        var machine = new StateMachine<State, Trigger>(State.Offline)
            .Configure(State.Offline, Trigger.Connect, State.Online)
            .Configure(State.Online, Trigger.Start, State.Busy);

        await machine.FireAsync(Trigger.Connect);
        await machine.FireAsync(Trigger.Start);

        Assert.Equal(State.Busy, machine.State);
    }
}
