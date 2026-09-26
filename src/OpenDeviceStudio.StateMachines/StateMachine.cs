using System.Runtime.ExceptionServices;

namespace OpenDeviceStudio.StateMachines;

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
    private readonly object _gate = new();
    private TState _state;

    public StateMachine(TState initialState) => _state = initialState;

    public TState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public event Action<TState, TState, TTrigger>? Transitioned;

    public StateMachine<TState, TTrigger> Configure(
        TState from,
        TTrigger trigger,
        TState to,
        Func<bool>? guard = null,
        Func<CancellationToken, Task>? action = null)
    {
        var key = (from, trigger);
        lock (_gate)
        {
            if (!_transitions.TryAdd(
                    key,
                    new StateTransition<TState, TTrigger>(
                        from,
                        trigger,
                        to,
                        guard,
                        action)))
            {
                throw new InvalidOperationException(
                    $"Transition from '{from}' with trigger '{trigger}' already exists.");
            }
        }

        return this;
    }

    public bool CanFire(TTrigger trigger)
    {
        StateTransition<TState, TTrigger>? transition;
        lock (_gate)
            _transitions.TryGetValue((_state, trigger), out transition);

        return transition is not null &&
               (transition.Guard?.Invoke() ?? true);
    }

    public async Task<TState> FireAsync(
        TTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        StateTransition<TState, TTrigger> transition;
        TState previous;

        lock (_gate)
        {
            previous = _state;
            if (!_transitions.TryGetValue((previous, trigger), out var configured))
            {
                throw new InvalidOperationException(
                    $"No transition from '{previous}' with trigger '{trigger}'.");
            }

            transition = configured;
        }

        // Guards are application callbacks and therefore execute outside the state lock.
        if (!(transition.Guard?.Invoke() ?? true))
        {
            throw new InvalidOperationException(
                $"Guard rejected transition from '{previous}' with trigger '{trigger}'.");
        }

        lock (_gate)
        {
            if (!EqualityComparer<TState>.Default.Equals(_state, previous))
            {
                throw new InvalidOperationException(
                    $"State changed from '{previous}' to '{_state}' while transition '{trigger}' was being prepared.");
            }

            if (!_transitions.TryGetValue((previous, trigger), out var current) ||
                !ReferenceEquals(current, transition))
            {
                throw new InvalidOperationException(
                    $"Transition from '{previous}' with trigger '{trigger}' changed while it was being prepared.");
            }

            // The state transition itself is the atomic authority. Side effects and
            // notifications happen afterwards and are never allowed to hold this lock.
            _state = transition.To;
        }

        Exception? actionFailure = null;
        Exception? notificationFailure = null;

        try
        {
            if (transition.Action is not null)
            {
                await transition.Action(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            actionFailure = ex;
        }

        try
        {
            Transitioned?.Invoke(previous, transition.To, trigger);
        }
        catch (Exception ex)
        {
            notificationFailure = ex;
        }

        if (actionFailure is not null && notificationFailure is not null)
        {
            throw new AggregateException(
                "State transition committed, but both its action and notification failed.",
                actionFailure,
                notificationFailure);
        }

        if (actionFailure is not null)
            ExceptionDispatchInfo.Capture(actionFailure).Throw();

        if (notificationFailure is not null)
            ExceptionDispatchInfo.Capture(notificationFailure).Throw();

        return transition.To;
    }
}
