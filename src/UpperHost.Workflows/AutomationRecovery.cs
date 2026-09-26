using UpperHost.Control.State;

namespace UpperHost.Workflows;

public enum AutomationRecoveryDecision
{
    Reset,
    Restart,
    ResumeSafeCheckpoint,
    ManualIntervention
}

public sealed record AutomationRecoveryEvidence(
    string ExecutionId,
    string WorkflowId,
    string WorkflowVersion,
    string RecipeHash,
    string? LastSafeCheckpoint,
    bool HasUnknownPhysicalOutcome,
    IReadOnlyList<string> CommandExecutionIds,
    IReadOnlyList<string> RequiredDeviceIds,
    string? FailureReason,
    long LastJournalSequence);

public sealed record AutomationReconciliationResult(
    bool Reconciled,
    bool PhysicalStateMatchesExpected,
    string? Detail = null);

public sealed record AutomationRecoveryResult(
    AutomationRecoveryDecision Decision,
    AutomationReconciliationResult Reconciliation,
    string? SafeCheckpoint,
    string? Message = null);

public interface IAutomationRecoveryReconciler
{
    ValueTask<AutomationReconciliationResult> ReconcileAsync(
        AutomationRecoveryEvidence evidence,
        CancellationToken cancellationToken = default);
}

public sealed class ConservativeAutomationRecoveryReconciler : IAutomationRecoveryReconciler
{
    public ValueTask<AutomationReconciliationResult> ReconcileAsync(
        AutomationRecoveryEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new AutomationReconciliationResult(
                Reconciled: false,
                PhysicalStateMatchesExpected: false,
                "No product-specific authoritative device reconcile was registered."));
    }
}

/// <summary>
/// Recovery adapter for the #63 DeviceControlStateRegistry authority. The product callback must
/// reconnect/read back each device and publish the resulting readiness into the registry.
/// Journal evidence is never treated as physical truth.
/// </summary>
public sealed class DeviceControlStateRecoveryReconciler : IAutomationRecoveryReconciler
{
    private readonly DeviceControlStateRegistry _registry;
    private readonly Func<string, CancellationToken, Task> _rehydrate;

    public DeviceControlStateRecoveryReconciler(
        DeviceControlStateRegistry registry,
        Func<string, CancellationToken, Task> rehydrate)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _rehydrate = rehydrate ?? throw new ArgumentNullException(nameof(rehydrate));
    }

    public async ValueTask<AutomationReconciliationResult> ReconcileAsync(
        AutomationRecoveryEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        foreach (var deviceId in evidence.RequiredDeviceIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _rehydrate(deviceId, cancellationToken).ConfigureAwait(false);

            var readiness = _registry.GetReadiness(deviceId);
            if (!readiness.IsReady)
            {
                return new AutomationReconciliationResult(
                    Reconciled: false,
                    PhysicalStateMatchesExpected: false,
                    $"Device '{deviceId}' is '{readiness.State}' after authoritative rehydrate/readback.");
            }
        }

        return new AutomationReconciliationResult(
            Reconciled: true,
            PhysicalStateMatchesExpected: true,
            "All required devices are Ready after authoritative #63 rehydrate/readback.");
    }
}
