using UpperHost.Control.Commands;

namespace UpperHost.Tests;

public sealed class CommandDispatchContractTests
{
    [Fact]
    public void Resource_set_is_canonical_and_deduplicated()
    {
        var device = new CommandResourceKey(CommandResourceKind.Device, "device-1");
        var connection = new CommandResourceKey(CommandResourceKind.Connection, "connection-1");

        var resources = new CommandResourceSet([device, connection, device]);

        Assert.Equal(2, resources.Count);
        Assert.Equal(connection, resources[0]);
        Assert.Equal(device, resources[1]);
    }

    [Fact]
    public void Resource_key_rejects_blank_value()
    {
        Assert.Throws<ArgumentException>(
            () => new CommandResourceKey(CommandResourceKind.Device, " "));
    }

    [Fact]
    public void Dispatcher_options_reject_invalid_capacity()
    {
        var options = new CommandDispatcherOptions
        {
            Capacity = 4,
            PerResourceCapacity = 5
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    [Fact]
    public void Dispatcher_options_allow_infinite_queue_wait()
    {
        var options = new CommandDispatcherOptions
        {
            DefaultQueueWaitTimeout = Timeout.InfiniteTimeSpan
        };

        options.Validate();
    }

    [Fact]
    public void Automatic_retry_requires_explicit_safe_idempotent_policy()
    {
        var safe = CommandSafetyPolicy.SafeRead with { RetryAllowed = true };
        var nonIdempotent = new CommandSafetyPolicy(
            CommandAccessMode.Mutating,
            CommandIdempotency.NonIdempotent,
            CommandHazardClass.Standard,
            RetryAllowed: true);
        var motion = new CommandSafetyPolicy(
            CommandAccessMode.Mutating,
            CommandIdempotency.Idempotent,
            CommandHazardClass.Motion,
            RetryAllowed: true);

        Assert.True(safe.CanAutomaticallyRetry);
        Assert.False(nonIdempotent.CanAutomaticallyRetry);
        Assert.False(motion.CanAutomaticallyRetry);
    }

    [Fact]
    public void Unknown_outcome_is_a_terminal_command_status()
    {
        Assert.True(Enum.IsDefined(CommandExecutionStatus.UnknownOutcome));
    }
}
