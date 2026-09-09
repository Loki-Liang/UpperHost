using UpperHost.Abstractions.Workflows;
using UpperHost.Control.Commands;
using UpperHost.Control.Interlocks;
using UpperHost.StateMachines;
using UpperHost.Workflows;

namespace UpperHost.Sample.AutomationStation;

public sealed class DoorClosedInterlock(SafetyDoorDevice door) : IInterlock<AxisCommand>
{
    public string Name => "safety-door-closed";

    public ValueTask<InterlockDecision> CheckAsync(AxisCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            door.IsClosed
                ? InterlockDecision.Pass()
                : InterlockDecision.Block("safety_door_open", "Axis motion is blocked while the safety door is open."));
    }
}

public enum StationState
{
    Idle,
    Preparing,
    Running,
    Completed,
    Faulted
}

public enum StationTrigger
{
    Start,
    BeginCycle,
    Complete,
    Fail,
    Reset
}

public sealed class AutomationStation
{
    private readonly WorkflowRunner _runner;
    private readonly CommandRuntime<AxisCommand, AxisResult> _axisRuntime;
    private readonly CameraDevice _camera;
    private readonly StateMachine<StationState, StationTrigger> _stateMachine;

    public AutomationStation(
        WorkflowRunner runner,
        CommandRuntime<AxisCommand, AxisResult> axisRuntime,
        CameraDevice camera)
    {
        _runner = runner;
        _axisRuntime = axisRuntime;
        _camera = camera;
        _stateMachine = new StateMachine<StationState, StationTrigger>(StationState.Idle)
            .Configure(StationState.Idle, StationTrigger.Start, StationState.Preparing)
            .Configure(StationState.Preparing, StationTrigger.BeginCycle, StationState.Running)
            .Configure(StationState.Running, StationTrigger.Complete, StationState.Completed)
            .Configure(StationState.Running, StationTrigger.Fail, StationState.Faulted)
            .Configure(StationState.Completed, StationTrigger.Reset, StationState.Idle)
            .Configure(StationState.Faulted, StationTrigger.Reset, StationState.Idle);
    }

    public StationState State => _stateMachine.State;

    public async Task<WorkflowRunResult> RunCycleAsync(CancellationToken cancellationToken = default)
    {
        await _stateMachine.FireAsync(StationTrigger.Start, cancellationToken).ConfigureAwait(false);
        await _stateMachine.FireAsync(StationTrigger.BeginCycle, cancellationToken).ConfigureAwait(false);

        var context = new WorkflowContext();
        var workflow = WorkflowDefinition.Create(
            "inspection-cycle",
            new AxisCommandStep("home-axis", _axisRuntime, new HomeAxisCommand()),
            new AxisCommandStep("move-to-inspection", _axisRuntime, new MoveAxisCommand(100)),
            new CaptureStep(_camera),
            new JudgeStep());

        var result = await _runner.RunAsync(workflow, context, cancellationToken).ConfigureAwait(false);
        await _stateMachine.FireAsync(
            result.Status == WorkflowStepStatus.Succeeded ? StationTrigger.Complete : StationTrigger.Fail,
            CancellationToken.None).ConfigureAwait(false);
        return result;
    }

    public Task ResetAsync(CancellationToken cancellationToken = default) =>
        _stateMachine.FireAsync(StationTrigger.Reset, cancellationToken);
}

internal sealed class AxisCommandStep(
    string name,
    CommandRuntime<AxisCommand, AxisResult> runtime,
    AxisCommand command) : IWorkflowStep
{
    public string Name => name;

    public async Task<WorkflowStepResult> ExecuteAsync(WorkflowContext context, CancellationToken cancellationToken = default)
    {
        var result = await runtime.ExecuteAsync(command, cancellationToken: cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? WorkflowStepResult.Success(result.Value?.Message)
            : WorkflowStepResult.Failure($"Axis command {result.Status}: {result.Code} - {result.Message}", result.Exception);
    }
}

internal sealed class CaptureStep(CameraDevice camera) : IWorkflowStep
{
    public string Name => "capture";

    public async Task<WorkflowStepResult> ExecuteAsync(WorkflowContext context, CancellationToken cancellationToken = default)
    {
        var capture = await camera.ExecuteAsync(new CaptureCommand(), cancellationToken).ConfigureAwait(false);
        context.Set("capture", capture);
        return WorkflowStepResult.Success($"Captured {capture.ImageId}.");
    }
}

internal sealed class JudgeStep : IWorkflowStep
{
    public string Name => "judge";

    public Task<WorkflowStepResult> ExecuteAsync(WorkflowContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capture = context.Get<CaptureResult>("capture");
        return Task.FromResult(
            capture is not null && capture.Quality >= 0.95
                ? WorkflowStepResult.Success($"PASS: {capture.ImageId}, quality={capture.Quality:0.00}.")
                : WorkflowStepResult.Failure("Inspection quality is below threshold."));
    }
}
