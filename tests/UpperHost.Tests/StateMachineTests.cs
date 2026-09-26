using UpperHost.StateMachines;

namespace UpperHost.Tests;

public sealed class StateMachineTests
{
    private enum State { Offline, Online, Busy 
    [Fact]
    public async Task Transition_event_can_reenter_without_holding_state_gate()
    {
        var machine = new StateMachine<State, Trigger>(State.Offline)
            .Configure(State.Offline, Trigger.Connect, State.Online)
            .Configure(State.Online, Trigger.Start, State.Busy);

        Task<State>? nested = null;
        machine.Transitioned += (_, _, trigger) =>
        {
            if (trigger == Trigger.Connect)
                nested = machine.FireAsync(Trigger.Start);
        };

        await machine.FireAsync(Trigger.Connect);
        Assert.NotNull(nested);
        await nested!;

        Assert.Equal(State.Busy, machine.State);
    }

    [Fact]
    public async Task Transition_action_reentry_fails_fast_instead_of_deadlocking()
    {
        StateMachine<State, Trigger>? machine = null;
        machine = new StateMachine<State, Trigger>(State.Offline)
            .Configure(
                State.Offline,
                Trigger.Connect,
                State.Online,
                action: _ => machine!.FireAsync(Trigger.Connect));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => machine.FireAsync(Trigger.Connect));

        Assert.Equal(State.Offline, machine.State);
    }
}
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
