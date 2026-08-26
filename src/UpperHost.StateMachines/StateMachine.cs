namespace UpperHost.StateMachines;

public sealed record StateTransition<TState, TTrigger>(
    TState From,
    TTrigger Trigger,
    TState To,
    Func<bool>? Guard = null,
    Func<CancellationToken, Task>? Action = null)
    where TState : notnull
    where TTrigger : notnull;

public sealed class StateMachine<TState, TTrigger>
    where TState : notnull
    where TTrigger : notnull
{
    private readonly Dictionary<(TState State, TTrigger Trigger), StateTransition<TState, TTrigger>> _transitions = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public StateMachine(TState initialState) => State = initialState;

    public TState State { get; private set; }

    public event Action<TState, TState, TTrigger>? Transitioned;

    public StateMachine<TState, TTrigger> Configure(
        TState from,
        TTrigger trigger,
        TState to,
        Func<bool>? guard = null,
        Func<CancellationToken, Task>? action = null)
    {
        var key = (from, trigger);
        if (!_transitions.TryAdd(key, new StateTransition<TState, TTrigger>(from, trigger, to, guard, action)))
            throw new InvalidOperationException($"Transition from '{from}' with trigger '{trigger}' already exists.");
        return this;
    }

    public bool CanFire(TTrigger trigger) =>
        _transitions.TryGetValue((State, trigger), out var transition) && (transition.Guard?.Invoke() ?? true);

    public async Task<TState> FireAsync(TTrigger trigger, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_transitions.TryGetValue((State, trigger), out var transition))
                throw new InvalidOperationException($"No transition from '{State}' with trigger '{trigger}'.");

            if (!(transition.Guard?.Invoke() ?? true))
                throw new InvalidOperationException($"Guard rejected transition from '{State}' with trigger '{trigger}'.");

            var previous = State;
            if (transition.Action is not null)
                await transition.Action(cancellationToken).ConfigureAwait(false);

            State = transition.To;
            Transitioned?.Invoke(previous, State, trigger);
            return State;
        }
        finally
        {
            _gate.Release();
        }
    }
}
