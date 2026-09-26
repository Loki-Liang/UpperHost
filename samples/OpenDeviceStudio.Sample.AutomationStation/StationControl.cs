using OpenDeviceStudio.Abstractions.Workflows;
using OpenDeviceStudio.Control.Commands;
using OpenDeviceStudio.Control.Interlocks;
using OpenDeviceStudio.Control.Scheduling;
using OpenDeviceStudio.StateMachines;
using OpenDeviceStudio.Workflows;

namespace OpenDeviceStudio.Sample.AutomationStation;

public sealed class DoorClosedInterlock(SafetyDoorDevice door) : IInterlock<AxisCommand>
{
    public string Name => "safety-door-closed";

    public ValueTask<InterlockDecision> CheckAsync(
        AxisCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            door.IsClosed
                ? InterlockDecision.Pass()
                : InterlockDecision.Block(
                    "safety_door_open",
                    "Axis motion is blocked while the safety door is open."));
    }
}

public enum StationState
{
    Idle,
    Preparing,
    Running,
    Paused,
    Stopping,
    Recovering,
    Completed,
    Faulted
}

public enum StationTrigger
{
    Prepare,
    Run,
    Pause,
    Resume,
    Stop,
    Complete,
    Fail,
    Recover,
    Reset
}

public sealed class AutomationStation : IWorkflowStationStateAuthority
{
    private static readonly WorkflowDataKey<CaptureResult> CaptureKey =
        new("inspection.capture");

    private static readonly CommandSafetyMetadata AxisMotionSafety =
        new(
            ReadOnly: false,
            Idempotent: false,
            Motion: true,
            Hazardous: false,
            RetryAllowed: false);

    private readonly WorkflowExecutionCoordinator _coordinator;
    private readonly BoundedCommandDispatcher<AxisCommand, AxisResult> _axisDispatcher;
    private readonly BoundedCommandDispatcher<CaptureCommand, CaptureResult> _cameraDispatcher;
    private readonly AxisDevice _axis;
    private readonly StateMachine<StationState, StationTrigger> _stateMachine;

    public AutomationStation(
        WorkflowExecutionCoordinator coordinator,
        BoundedCommandDispatcher<AxisCommand, AxisResult> axisDispatcher,
        BoundedCommandDispatcher<CaptureCommand, CaptureResult> cameraDispatcher,
        AxisDevice axis)
    {
        _coordinator = coordinator;
        _axisDispatcher = axisDispatcher;
        _cameraDispatcher = cameraDispatcher;
        _axis = axis;

        _stateMachine = new StateMachine<StationState, StationTrigger>(StationState.Idle)
            .Configure(StationState.Idle, StationTrigger.Prepare, StationState.Preparing)
            .Configure(StationState.Idle, StationTrigger.Stop, StationState.Stopping)
            .Configure(StationState.Idle, StationTrigger.Fail, StationState.Faulted)
            .Configure(StationState.Preparing, StationTrigger.Run, StationState.Running)
            .Configure(StationState.Running, StationTrigger.Pause, StationState.Paused)
            .Configure(StationState.Paused, StationTrigger.Resume, StationState.Running)
            .Configure(StationState.Preparing, StationTrigger.Stop, StationState.Stopping)
            .Configure(StationState.Running, StationTrigger.Stop, StationState.Stopping)
            .Configure(StationState.Paused, StationTrigger.Stop, StationState.Stopping)
            .Configure(StationState.Stopping, StationTrigger.Complete, StationState.Completed)
            .Configure(StationState.Running, StationTrigger.Complete, StationState.Completed)
            .Configure(StationState.Preparing, StationTrigger.Fail, StationState.Faulted)
            .Configure(StationState.Running, StationTrigger.Fail, StationState.Faulted)
            .Configure(StationState.Paused, StationTrigger.Fail, StationState.Faulted)
            .Configure(StationState.Stopping, StationTrigger.Fail, StationState.Faulted)
            .Configure(StationState.Faulted, StationTrigger.Recover, StationState.Recovering)
            .Configure(StationState.Completed, StationTrigger.Reset, StationState.Idle)
            .Configure(StationState.Faulted, StationTrigger.Reset, StationState.Idle)
            .Configure(StationState.Recovering, StationTrigger.Reset, StationState.Idle);
    }

    public StationState State => _stateMachine.State;

    public WorkflowExecutionHandle StartCycle(
        CancellationToken cancellationToken = default)
    {
        if (State != StationState.Idle)
        {
            throw new InvalidOperationException(
                $"Station must be Idle before a cycle starts; current state is '{State}'.");
        }

        var definition = BuildWorkflow();
        var recipe = WorkflowRecipeSnapshot.Create(
            "inspection-default",
            "1",
            """{"inspectionPosition":100.0,"minimumQuality":0.95}""");

        return _coordinator.Start(
            new WorkflowExecutionRequest(
                definition,
                recipe,
                StationId: "automation-station",
                Mode: "Auto",
                StationStateAuthority: this),
            cancellationToken);
    }

    public async Task<WorkflowExecutionResult> RunCycleAsync(
        CancellationToken cancellationToken = default) =>
        await StartCycle(cancellationToken)
            .Completion
            .ConfigureAwait(false);

    public Task ResetAsync(CancellationToken cancellationToken = default) =>
        _stateMachine.FireAsync(StationTrigger.Reset, cancellationToken);

    public ValueTask OnExecutionTransitionAsync(
        string executionId,
        WorkflowExecutionStatus from,
        WorkflowExecutionStatus to,
        string? reason,
        CancellationToken cancellationToken = default) =>
        new(ApplyExecutionTransitionAsync(to, cancellationToken));

    private WorkflowExecutionDefinition BuildWorkflow()
    {
        var motionLease =
            new WorkflowResourceRequirement("fixture", "inspection-motion");

        return new WorkflowExecutionDefinition(
            "inspection-cycle",
            "2",
            new WorkflowSequenceNode(
                "inspection-cycle",
                [
                    new WorkflowActionNode(
                        "home-axis",
                        (context, cancellationToken) =>
                            DispatchAxisAsync(
                                context,
                                new HomeAxisCommand(),
                                cancellationToken),
                        new WorkflowStepPolicy(
                            SideEffecting: true,
                            Resources: [motionLease],
                            CompletionCondition: (_, _, _) =>
                                ValueTask.FromResult(_axis.Homed))),
                    new WorkflowActionNode(
                        "move-to-inspection",
                        (context, cancellationToken) =>
                            DispatchAxisAsync(
                                context,
                                new MoveAxisCommand(100),
                                cancellationToken),
                        new WorkflowStepPolicy(
                            SideEffecting: true,
                            Resources: [motionLease],
                            CompletionCondition: (_, _, _) =>
                                ValueTask.FromResult(
                                    Math.Abs(_axis.Position - 100) < 0.001))),
                    new WorkflowSafeCheckpointNode("at-inspection"),
                    new WorkflowActionNode(
                        "capture",
                        CaptureAsync,
                        new WorkflowStepPolicy(
                            SideEffecting: true,
                            Resources:
                            [
                                new WorkflowResourceRequirement(
                                    "fixture",
                                    "inspection-camera")
                            ])),
                    new WorkflowActionNode(
                        "judge",
                        JudgeAsync,
                        new WorkflowStepPolicy(
                            Precondition: (context, _) =>
                                ValueTask.FromResult(
                                    context.Data.TryGet(CaptureKey, out _))))
                ]));
    }

    private async Task<WorkflowStepResult> DispatchAxisAsync(
        WorkflowExecutionContext context,
        AxisCommand command,
        CancellationToken cancellationToken)
    {
        var result = await _axisDispatcher.EnqueueAsync(
            command,
            new CommandDispatchOptions(
                ResourceClaims:
                [
                    new CommandResourceClaim(
                        new CommandResourceKey("device", "axis-x"),
                        CommandResourceAccess.Exclusive)
                ],
                Safety: AxisMotionSafety),
            cancellationToken).ConfigureAwait(false);

        context.RecordCommandExecutionId(result.ExecutionId);

        if (result.Status == CommandExecutionStatus.UnknownOutcome)
        {
            throw new WorkflowPhysicalOutcomeUnknownException(
                result.Message ?? "Axis command physical outcome is unknown.");
        }

        return result.IsSuccess
            ? WorkflowStepResult.Success(result.Value?.Message)
            : WorkflowStepResult.Failure(
                $"Axis command {result.Status}: {result.Code} - {result.Message}",
                result.Exception);
    }

    private async Task<WorkflowStepResult> CaptureAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        var result = await _cameraDispatcher.EnqueueAsync(
            new CaptureCommand(),
            new CommandDispatchOptions(
                ResourceClaims:
                [
                    new CommandResourceClaim(
                        new CommandResourceKey("device", "camera-1"),
                        CommandResourceAccess.Exclusive)
                ],
                Safety: CommandSafetyMetadata.MutatingNonIdempotent),
            cancellationToken).ConfigureAwait(false);

        context.RecordCommandExecutionId(result.ExecutionId);

        if (result.Status == CommandExecutionStatus.UnknownOutcome)
        {
            throw new WorkflowPhysicalOutcomeUnknownException(
                result.Message ?? "Camera command physical outcome is unknown.");
        }

        if (!result.IsSuccess || result.Value is null)
        {
            return WorkflowStepResult.Failure(
                $"Capture command {result.Status}: {result.Code} - {result.Message}",
                result.Exception);
        }

        context.Data.Set(CaptureKey, result.Value);
        return WorkflowStepResult.Success($"Captured {result.Value.ImageId}.");
    }

    private static Task<WorkflowStepResult> JudgeAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var capture = context.Data.GetRequired(CaptureKey);
        return Task.FromResult(
            capture.Quality >= 0.95
                ? WorkflowStepResult.Success(
                    $"PASS: {capture.ImageId}, quality={capture.Quality:0.00}.")
                : WorkflowStepResult.Failure(
                    "Inspection quality is below threshold."));
    }

    private Task ApplyExecutionTransitionAsync(
        WorkflowExecutionStatus target,
        CancellationToken cancellationToken) =>
        target switch
        {
            WorkflowExecutionStatus.Preparing =>
                FireIfPossibleAsync(StationTrigger.Prepare, cancellationToken),
            WorkflowExecutionStatus.Running when State == StationState.Paused =>
                FireIfPossibleAsync(StationTrigger.Resume, cancellationToken),
            WorkflowExecutionStatus.Running =>
                FireIfPossibleAsync(StationTrigger.Run, cancellationToken),
            WorkflowExecutionStatus.Paused =>
                FireIfPossibleAsync(StationTrigger.Pause, cancellationToken),
            WorkflowExecutionStatus.Stopping =>
                FireIfPossibleAsync(StationTrigger.Stop, cancellationToken),
            WorkflowExecutionStatus.Completed or WorkflowExecutionStatus.Stopped =>
                FireIfPossibleAsync(StationTrigger.Complete, cancellationToken),
            WorkflowExecutionStatus.Failed or
                WorkflowExecutionStatus.Aborted or
                WorkflowExecutionStatus.RecoveryRequired =>
                FireIfPossibleAsync(StationTrigger.Fail, cancellationToken),
            WorkflowExecutionStatus.Recovering =>
                FireIfPossibleAsync(StationTrigger.Recover, cancellationToken),
            _ => Task.CompletedTask
        };

    private Task FireIfPossibleAsync(
        StationTrigger trigger,
        CancellationToken cancellationToken)
    {
        if (!_stateMachine.CanFire(trigger))
        {
            throw new InvalidOperationException(
                $"Station state '{State}' cannot accept workflow trigger '{trigger}'.");
        }

        return _stateMachine.FireAsync(trigger, cancellationToken);
    }
}
