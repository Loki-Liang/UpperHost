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
    private bool _transitionInProgress;

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
        !_transitionInProgress &&
        _transitions.TryGetValue((State, trigger), out var transition) &&
        (transition.Guard?.Invoke() ?? true);

    public async Task<TState> FireAsync(TTrigger trigger, CancellationToken cancellationToken = default)
    {
        StateTransition<TState, TTrigger> transition;
        TState previous;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_transitionInProgress)
                throw new InvalidOperationException("A state transition side effect is already in progress.");

            if (!_transitions.TryGetValue((State, trigger), out transition!))
                throw new InvalidOperationException($"No transition from '{State}' with trigger '{trigger}'.");

            if (!(transition.Guard?.Invoke() ?? true))
                throw new InvalidOperationException($"Guard rejected transition from '{State}' with trigger '{trigger}'.");

            previous = State;
            _transitionInProgress = true;
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            if (transition.Action is not null)
                await transition.Action(cancellationToken).ConfigureAwait(false);

            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!EqualityComparer<TState>.Default.Equals(State, previous))
                    throw new InvalidOperationException("State changed while a transition side effect was running.");

                State = transition.To;
                _transitionInProgress = false;
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _transitionInProgress = false;
            }
            finally
            {
                _gate.Release();
            }

            throw;
        }

        Transitioned?.Invoke(previous, State, trigger);
        return State;
    }
}
