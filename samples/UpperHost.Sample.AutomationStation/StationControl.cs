using UpperHost.Control.Commands;
using UpperHost.Control.Interlocks;
using UpperHost.Control.Scheduling;
using UpperHost.Workflows;

namespace UpperHost.Sample.AutomationStation;

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

public sealed class AutomationStation
{
    private static readonly CommandResourceKey AxisResource =
        new("device", "axis-x");
    private static readonly CommandResourceKey CameraResource =
        new("device", "camera-1");

    private static readonly CommandSafetyMetadata MotionSafety =
        new(
            ReadOnly: false,
            Idempotent: false,
            Motion: true,
            Hazardous: false,
            RetryAllowed: false);

    private readonly AutomationExecutionCoordinator _coordinator;
    private readonly BoundedCommandDispatcher<AxisCommand, AxisResult> _axisDispatcher;
    private readonly BoundedCommandDispatcher<CaptureCommand, CaptureResult> _cameraDispatcher;
    private readonly AutomationExecutionPlan _plan;

    public AutomationStation(
        AutomationExecutionCoordinator coordinator,
        BoundedCommandDispatcher<AxisCommand, AxisResult> axisDispatcher,
        BoundedCommandDispatcher<CaptureCommand, CaptureResult> cameraDispatcher)
    {
        _coordinator = coordinator;
        _axisDispatcher = axisDispatcher;
        _cameraDispatcher = cameraDispatcher;
        _plan = AutomationExecutionPlan.Compile(BuildDefinition());
    }

    public AutomationStationState State => _coordinator.StationState;

    public async Task<AutomationExecutionResult> RunCycleAsync(
        InspectionRecipe recipe,
        CancellationToken cancellationToken = default)
    {
        var snapshot = AutomationRecipeSnapshot.Create(
            "inspection-cycle",
            recipe.Version,
            recipe);

        var start = _coordinator.TryStart(_plan, snapshot, AutomationMode.Auto);
        if (!start.Accepted || start.Execution is null)
            throw new InvalidOperationException(
                $"Automation cycle rejected: {start.Code} - {start.Message}");

        return await start.Execution.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public AutomationControlResult Reset() => _coordinator.TryResetStation();

    private AutomationWorkflowDefinition BuildDefinition() =>
        new(
            "inspection-cycle",
            "2.0",
            new AutomationSequenceNode(
                "cycle",
                AxisStep("home-axis", new HomeAxisCommand()),
                new AutomationCheckpointNode(
                    "home-safe",
                    AutomationCheckpointKind.SafeRecovery),
                AxisStep(
                    "move-to-inspection",
                    new MoveAxisCommand(100),
                    pauseBoundaryAfter: true),
                new AutomationCheckpointNode(
                    "inspection-safe-pause",
                    AutomationCheckpointKind.SafePause),
                new AutomationParallelNode(
                    "inspection-parallel",
                    maxConcurrency: 2,
                    AutomationJoinMode.WaitAll,
                    CaptureStep(),
                    MetadataStep()),
                JudgeStep()));

    private AutomationActionNode AxisStep(
        string id,
        AxisCommand command,
        bool pauseBoundaryAfter = false) =>
        new(
            id,
            new AutomationStepDescriptor(
                async (context, cancellationToken) =>
                {
                    var result = await context.DispatchAsync(
                        _axisDispatcher,
                        command,
                        new CommandDispatchOptions(
                            ResourceClaims:
                            [
                                new CommandResourceClaim(
                                    AxisResource,
                                    CommandResourceAccess.Exclusive)
                            ],
                            Safety: MotionSafety),
                        cancellationToken).ConfigureAwait(false);

                    return AutomationStepResult.FromCommand(result);
                },
                new AutomationStepPolicy(
                    Timeout: TimeSpan.FromSeconds(5),
                    Resources:
                    [
                        new CommandResourceClaim(
                            AxisResource,
                            CommandResourceAccess.Exclusive)
                    ],
                    PauseBoundaryAfter: pauseBoundaryAfter,
                    CancelOnStop: false)));

    private AutomationActionNode CaptureStep() =>
        new(
            "capture",
            new AutomationStepDescriptor(
                async (context, cancellationToken) =>
                {
                    var result = await context.DispatchAsync(
                        _cameraDispatcher,
                        new CaptureCommand(),
                        new CommandDispatchOptions(
                            ResourceClaims:
                            [
                                new CommandResourceClaim(
                                    CameraResource,
                                    CommandResourceAccess.Exclusive)
                            ],
                            Safety: CommandSafetyMetadata.MutatingNonIdempotent),
                        cancellationToken).ConfigureAwait(false);

                    if (result.IsSuccess && result.Value is not null)
                        context.SetOutput("result", result.Value);

                    return AutomationStepResult.FromCommand(result);
                },
                new AutomationStepPolicy(
                    Timeout: TimeSpan.FromSeconds(5),
                    Resources:
                    [
                        new CommandResourceClaim(
                            CameraResource,
                            CommandResourceAccess.Exclusive)
                    ])));

    private static AutomationActionNode MetadataStep() =>
        new(
            "cycle-metadata",
            new AutomationStepDescriptor(
                (context, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    context.SetOutput("capturedAt", DateTimeOffset.UtcNow);
                    return Task.FromResult(AutomationStepResult.Success());
                }));

    private static AutomationActionNode JudgeStep() =>
        new(
            "judge",
            new AutomationStepDescriptor(
                (context, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var capture = context.Data.GetRequired(
                        new AutomationDataKey<CaptureResult>("capture", "result"));

                    return Task.FromResult(
                        capture.Quality >= 0.95
                            ? AutomationStepResult.Success(
                                $"PASS: {capture.ImageId}, quality={capture.Quality:0.00}.")
                            : AutomationStepResult.Failure(
                                "Inspection quality is below threshold."));
                }));
}

public sealed record InspectionRecipe(
    string Version,
    double InspectionPosition,
    double MinimumQuality);
