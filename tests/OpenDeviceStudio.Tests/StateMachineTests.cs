using OpenDeviceStudio.StateMachines;

namespace OpenDeviceStudio.Tests;

public sealed class StateMachineTests
{
    private enum State
    {
        Offline,
        Online,
        Busy,
        Completed
    }

    private enum Trigger
    {
        Connect,
        Start,
        Complete
    }

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

    [Fact]
    public async Task Transition_callback_can_reenter_without_deadlock()
    {
        var machine = new StateMachine<State, Trigger>(State.Offline)
            .Configure(State.Offline, Trigger.Connect, State.Online)
            .Configure(State.Online, Trigger.Start, State.Busy);

        machine.Transitioned += (_, to, _) =>
        {
            if (to == State.Online)
                machine.FireAsync(Trigger.Start).GetAwaiter().GetResult();
        };

        await machine
            .FireAsync(Trigger.Connect)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(State.Busy, machine.State);
    }

    [Fact]
    public async Task Transition_action_does_not_hold_state_lock()
    {
        var actionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAction = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var machine = new StateMachine<State, Trigger>(State.Offline)
            .Configure(
                State.Offline,
                Trigger.Connect,
                State.Online,
                action: async cancellationToken =>
                {
                    actionStarted.TrySetResult();
                    await releaseAction.Task.WaitAsync(cancellationToken);
                })
            .Configure(State.Online, Trigger.Start, State.Busy);

        var connect = machine.FireAsync(Trigger.Connect);
        await actionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var start = machine.FireAsync(Trigger.Start);
        await start.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(State.Busy, machine.State);

        releaseAction.TrySetResult();
        await connect;
    }

    [Fact]
    public async Task Action_failure_does_not_roll_back_committed_transition()
    {
        var machine = new StateMachine<State, Trigger>(State.Offline)
            .Configure(
                State.Offline,
                Trigger.Connect,
                State.Online,
                action: static _ => throw new InvalidOperationException("post-transition failure"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => machine.FireAsync(Trigger.Connect));

        Assert.Equal("post-transition failure", exception.Message);
        Assert.Equal(State.Online, machine.State);
    }

    [Fact]
    public async Task Callback_failure_does_not_poison_future_transitions()
    {
        var machine = new StateMachine<State, Trigger>(State.Offline)
            .Configure(State.Offline, Trigger.Connect, State.Online)
            .Configure(State.Online, Trigger.Start, State.Busy);

        Action<State, State, Trigger> failing = static (_, _, _) =>
            throw new InvalidOperationException("observer failure");
        machine.Transitioned += failing;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => machine.FireAsync(Trigger.Connect));

        machine.Transitioned -= failing;

        await machine.FireAsync(Trigger.Start);
        Assert.Equal(State.Busy, machine.State);
    }
}
