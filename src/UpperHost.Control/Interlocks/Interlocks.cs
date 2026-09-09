using UpperHost.Control.Commands;

namespace UpperHost.Control.Interlocks;

public sealed record InterlockDecision(bool Satisfied, string? Code = null, string? Message = null)
{
    public static InterlockDecision Pass() => new(true);
    public static InterlockDecision Block(string code, string message) => new(false, code, message);
}

public interface IInterlock<in TCommand>
{
    string Name { get; }
    ValueTask<InterlockDecision> CheckAsync(TCommand command, CancellationToken cancellationToken = default);
}

public sealed class InterlockCommandGuard<TCommand> : ICommandGuard<TCommand>
{
    private readonly IReadOnlyList<IInterlock<TCommand>> _interlocks;

    public InterlockCommandGuard(IEnumerable<IInterlock<TCommand>> interlocks)
    {
        ArgumentNullException.ThrowIfNull(interlocks);
        _interlocks = interlocks.ToArray();
    }

    public string Name => "software-interlocks";

    public async ValueTask<CommandGuardDecision> EvaluateAsync(
        TCommand command,
        CancellationToken cancellationToken = default)
    {
        foreach (var interlock in _interlocks)
        {
            var decision = await interlock.CheckAsync(command, cancellationToken).ConfigureAwait(false);
            if (!decision.Satisfied)
            {
                return CommandGuardDecision.Reject(
                    decision.Code ?? "interlock_blocked",
                    decision.Message ?? $"Software interlock '{interlock.Name}' blocked the command.");
            }
        }

        return CommandGuardDecision.Allow();
    }
}
