using UpperHost.Abstractions.Devices;
using UpperHost.Control.Commands;
using UpperHost.Control.Interlocks;
using UpperHost.Control.Parameters;

namespace UpperHost.Tests;

public sealed class ControlRuntimeTests
{
    [Fact]
    public async Task Command_runtime_executes_allowed_command()
    {
        var target = new TestCommandTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target);

        var result = await runtime.ExecuteAsync(new TestCommand("run"));

        Assert.True(result.IsSuccess);
        Assert.Equal("run", result.Value);
        Assert.Equal(1, target.CallCount);
    }

    [Fact]
    public async Task Command_guard_rejects_before_device_execution()
    {
        var target = new TestCommandTarget();
        var runtime = new CommandRuntime<TestCommand, string>(target, [new RejectingGuard()]);

        var result = await runtime.ExecuteAsync(new TestCommand("blocked"));

        Assert.Equal(CommandExecutionStatus.Rejected, result.Status);
        Assert.Equal("test_blocked", result.Code);
        Assert.Equal(0, target.CallCount);
    }

    [Fact]
    public async Task Command_runtime_reports_timeout()
    {
        var runtime = new CommandRuntime<TestCommand, string>(new NeverCompletesTarget());

        var result = await runtime.ExecuteAsync(
            new TestCommand("slow"),
            new CommandExecutionOptions(TimeSpan.FromMilliseconds(50)));

        Assert.Equal(CommandExecutionStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task Interlock_guard_blocks_command()
    {
        var guard = new InterlockCommandGuard<TestCommand>([new DoorOpenInterlock()]);

        var decision = await guard.EvaluateAsync(new TestCommand("move"));

        Assert.False(decision.Allowed);
        Assert.Equal("door_open", decision.Code);
    }

    [Fact]
    public async Task Parameter_write_is_verified_by_readback()
    {
        var provider = new TestParameterProvider(20d);
        var service = new ParameterReadbackService(provider);

        var result = await service.WriteAsync("target", 30d);

        Assert.True(result.Verified);
        Assert.Equal(30d, result.ActualValue);
    }

    [Fact]
    public async Task Parameter_write_throws_on_readback_mismatch()
    {
        var provider = new TestParameterProvider(20d) { IgnoreWrites = true };
        var service = new ParameterReadbackService(provider);

        await Assert.ThrowsAsync<ParameterReadbackException>(() => service.WriteAsync("target", 30d));
    }

    private sealed record TestCommand(string Name);

    private sealed class TestCommandTarget : ICommandable<TestCommand, string>
    {
        public int CallCount { get; private set; }

        public Task<string> ExecuteAsync(TestCommand command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(command.Name);
        }
    }

    private sealed class NeverCompletesTarget : ICommandable<TestCommand, string>
    {
        public async Task<string> ExecuteAsync(TestCommand command, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return command.Name;
        }
    }

    private sealed class RejectingGuard : ICommandGuard<TestCommand>
    {
        public string Name => "reject-test";

        public ValueTask<CommandGuardDecision> EvaluateAsync(TestCommand command, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(CommandGuardDecision.Reject("test_blocked", "Blocked for test."));
    }

    private sealed class DoorOpenInterlock : IInterlock<TestCommand>
    {
        public string Name => "door";

        public ValueTask<InterlockDecision> CheckAsync(TestCommand command, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterlockDecision.Block("door_open", "Guard door is open."));
    }

    private sealed class TestParameterProvider(double value) : IParameterProvider
    {
        private double _value = value;
        public bool IgnoreWrites { get; init; }

        public Task<IReadOnlyList<DeviceParameterDescriptor>> GetParametersAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<DeviceParameterDescriptor> result =
            [
                new("target", "Target", DeviceParameterKind.Decimal, _value, "°C", 0, 100)
            ];
            return Task.FromResult(result);
        }

        public Task SetParameterAsync(string key, object? value, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IgnoreWrites)
                _value = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            return Task.CompletedTask;
        }
    }
}
