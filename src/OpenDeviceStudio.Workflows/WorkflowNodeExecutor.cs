using System.Collections.Concurrent;
using OpenDeviceStudio.Abstractions.Workflows;

namespace OpenDeviceStudio.Workflows;

public sealed partial class WorkflowExecutionCoordinator
{
    private async Task<NodeResultCore> ExecuteNodeAsync(
        WorkflowNode node,
        WorkflowExecutionContext parentContext,
        WorkflowExecutionRequest request,
        WorkflowExecutionControl control,
        OutcomeCollector outcomes,
        CancellationToken cancellationToken)
    {
        if (control.IsAbortRequested)
            return new NodeResultCore(WorkflowNodeStatus.Aborted, Message: "Abort requested.");

        if (control.IsStopRequested)
        {
            await EnsureStoppingAsync(request, control).ConfigureAwait(false);
            return new NodeResultCore(WorkflowNodeStatus.Stopped, Message: "Stop requested.");
        }

        var started = _timeProvider.GetTimestamp();
        var context = parentContext.ForNode(node.NodeId);

        await AppendJournalAsync(
            control,
            "node_started",
            control.State,
            node.NodeId,
            null,
            request.RecipeSnapshot.Hash,
            CancellationToken.None).ConfigureAwait(false);

        NodeResultCore result;
        try
        {
            result = node switch
            {
                WorkflowActionNode action => await ExecuteActionAsync(
                    action,
                    context,
                    control,
                    cancellationToken).ConfigureAwait(false),

                WorkflowSequenceNode sequence => await ExecuteSequenceAsync(
                    sequence,
                    context,
                    request,
                    control,
                    outcomes,
                    cancellationToken).ConfigureAwait(false),

                WorkflowParallelNode parallel => await ExecuteParallelAsync(
                    parallel,
                    context,
                    request,
                    control,
                    outcomes,
                    cancellationToken).ConfigureAwait(false),

                WorkflowSafeCheckpointNode checkpoint => await ExecuteCheckpointAsync(
                    checkpoint,
                    request,
                    control).ConfigureAwait(false),

                _ => throw new NotSupportedException(
                    $"Workflow node type '{node.GetType().FullName}' is not supported.")
            };
        }
        catch (WorkflowPhysicalOutcomeUnknownException ex)
        {
            result = new NodeResultCore(
                WorkflowNodeStatus.RecoveryRequired,
                Message: ex.Message,
                Exception: ex);
        }
        catch (OperationCanceledException ex) when (control.IsAbortRequested)
        {
            result = new NodeResultCore(
                WorkflowNodeStatus.Aborted,
                Message: "Abort requested.",
                Exception: ex);
        }
        catch (Exception ex)
        {
            result = new NodeResultCore(
                WorkflowNodeStatus.Failed,
                Message: ex.Message,
                Exception: ex);
        }

        var duration = _timeProvider.GetElapsedTime(started);
        outcomes.Add(
            new WorkflowNodeOutcome(
                node.NodeId,
                result.Status,
                result.Attempts,
                duration,
                result.Message,
                result.Exception));

        WorkflowExecutionTelemetry.NodeDurationSeconds.Record(
            duration.TotalSeconds,
            WorkflowExecutionTelemetry.NodeTags(node, result.Status));

        await AppendJournalAsync(
            control,
            "node_completed",
            control.State,
            node.NodeId,
            $"{result.Status}: {result.Message}",
            request.RecipeSnapshot.Hash,
            CancellationToken.None).ConfigureAwait(false);

        return result;
    }

    private async Task<NodeResultCore> ExecuteSequenceAsync(
        WorkflowSequenceNode sequence,
        WorkflowExecutionContext context,
        WorkflowExecutionRequest request,
        WorkflowExecutionControl control,
        OutcomeCollector outcomes,
        CancellationToken cancellationToken)
    {
        foreach (var child in sequence.Children)
        {
            if (control.IsAbortRequested)
                return new NodeResultCore(WorkflowNodeStatus.Aborted, Message: "Abort requested.");

            if (control.IsStopRequested)
            {
                await EnsureStoppingAsync(request, control).ConfigureAwait(false);
                return new NodeResultCore(
                    WorkflowNodeStatus.Stopped,
                    Message: "Stop requested; no new sequence node was started.");
            }

            var result = await ExecuteNodeAsync(
                child,
                context,
                request,
                control,
                outcomes,
                cancellationToken).ConfigureAwait(false);

            if (result.Status != WorkflowNodeStatus.Succeeded)
                return result;
        }

        return new NodeResultCore(WorkflowNodeStatus.Succeeded);
    }

    private async Task<NodeResultCore> ExecuteParallelAsync(
        WorkflowParallelNode parallel,
        WorkflowExecutionContext context,
        WorkflowExecutionRequest request,
        WorkflowExecutionControl control,
        OutcomeCollector outcomes,
        CancellationToken cancellationToken)
    {
        using var limiter = new SemaphoreSlim(
            parallel.MaxConcurrency,
            parallel.MaxConcurrency);
        using var failFast = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var branchData = parallel.Children
            .Select(_ => context.Data.Fork())
            .ToArray();

        var tasks = parallel.Children
            .Select((child, index) => RunChildAsync(child, index))
            .ToArray();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var recovery = results.FirstOrDefault(
            static result => result.Status == WorkflowNodeStatus.RecoveryRequired);
        if (recovery is not null)
            return recovery;

        if (control.IsAbortRequested ||
            results.Any(static result => result.Status == WorkflowNodeStatus.Aborted))
        {
            return new NodeResultCore(
                WorkflowNodeStatus.Aborted,
                Message: "Parallel execution aborted.");
        }

        var failed = results.FirstOrDefault(
            static result => result.Status == WorkflowNodeStatus.Failed);
        if (failed is not null)
            return failed;

        if (control.IsStopRequested ||
            results.Any(static result => result.Status == WorkflowNodeStatus.Stopped))
        {
            return new NodeResultCore(
                WorkflowNodeStatus.Stopped,
                Message: "Parallel execution stopped.");
        }

        try
        {
            for (var i = 0; i < branchData.Length; i++)
                context.Data.MergeFrom(branchData[i], parallel.MergePolicy);
        }
        catch (Exception ex)
        {
            return new NodeResultCore(
                WorkflowNodeStatus.Failed,
                Message: ex.Message,
                Exception: ex);
        }

        return new NodeResultCore(WorkflowNodeStatus.Succeeded);

        async Task<NodeResultCore> RunChildAsync(WorkflowNode child, int index)
        {
            var branchToken = parallel.JoinPolicy == WorkflowParallelJoinPolicy.FailFast
                ? failFast.Token
                : cancellationToken;

            using var admission = CancellationTokenSource.CreateLinkedTokenSource(
                branchToken,
                control.StopToken);

            try
            {
                await limiter.WaitAsync(admission.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (control.IsAbortRequested)
            {
                return new NodeResultCore(
                    WorkflowNodeStatus.Aborted,
                    Message: "Abort requested before parallel branch admission.");
            }
            catch (OperationCanceledException) when (control.IsStopRequested)
            {
                return new NodeResultCore(
                    WorkflowNodeStatus.Stopped,
                    Message: "Stop requested before parallel branch admission.");
            }
            catch (OperationCanceledException) when (
                parallel.JoinPolicy == WorkflowParallelJoinPolicy.FailFast &&
                failFast.IsCancellationRequested)
            {
                return new NodeResultCore(
                    WorkflowNodeStatus.Stopped,
                    Message: "Parallel branch cancelled by fail-fast join policy.");
            }

            try
            {
                if (control.IsAbortRequested)
                    return new NodeResultCore(WorkflowNodeStatus.Aborted, Message: "Abort requested.");

                if (control.IsStopRequested)
                {
                    return new NodeResultCore(
                        WorkflowNodeStatus.Stopped,
                        Message: "Stop requested before parallel branch start.");
                }

                var branchContext = context.ForNode(child.NodeId, branchData[index]);
                var result = await ExecuteNodeAsync(
                    child,
                    branchContext,
                    request,
                    control,
                    outcomes,
                    branchToken).ConfigureAwait(false);

                if (parallel.JoinPolicy == WorkflowParallelJoinPolicy.FailFast &&
                    result.Status != WorkflowNodeStatus.Succeeded)
                {
                    failFast.Cancel();
                }

                return result;
            }
            finally
            {
                limiter.Release();
            }
        }
    }

    private async Task<NodeResultCore> ExecuteCheckpointAsync(
        WorkflowSafeCheckpointNode checkpoint,
        WorkflowExecutionRequest request,
        WorkflowExecutionControl control)
    {
        control.SetLastSafeCheckpoint(checkpoint.NodeId);

        if (!control.TryEnterPaused(out var resumeTask))
            return new NodeResultCore(WorkflowNodeStatus.Succeeded);

        await NotifyStateAsync(
            request,
            control,
            WorkflowExecutionStatus.PauseRequested,
            WorkflowExecutionStatus.Paused,
            $"Paused at safe checkpoint '{checkpoint.NodeId}'.",
            CancellationToken.None).ConfigureAwait(false);

        await AppendJournalAsync(
            control,
            "safe_checkpoint",
            WorkflowExecutionStatus.Paused,
            checkpoint.NodeId,
            "Pause boundary reached.",
            request.RecipeSnapshot.Hash,
            CancellationToken.None).ConfigureAwait(false);

        await resumeTask.ConfigureAwait(false);

        if (control.IsAbortRequested)
            return new NodeResultCore(WorkflowNodeStatus.Aborted, Message: "Abort requested while paused.");

        if (control.IsStopRequested)
        {
            await EnsureStoppingAsync(request, control).ConfigureAwait(false);
            return new NodeResultCore(WorkflowNodeStatus.Stopped, Message: "Stop requested while paused.");
        }

        await NotifyStateAsync(
            request,
            control,
            WorkflowExecutionStatus.Paused,
            WorkflowExecutionStatus.Running,
            $"Resumed after checkpoint '{checkpoint.NodeId}'.",
            CancellationToken.None).ConfigureAwait(false);

        await AppendJournalAsync(
            control,
            "execution_resumed",
            WorkflowExecutionStatus.Running,
            checkpoint.NodeId,
            null,
            request.RecipeSnapshot.Hash,
            CancellationToken.None).ConfigureAwait(false);

        return new NodeResultCore(WorkflowNodeStatus.Succeeded);
    }

    private async Task<NodeResultCore> ExecuteActionAsync(
        WorkflowActionNode action,
        WorkflowExecutionContext context,
        WorkflowExecutionControl control,
        CancellationToken cancellationToken)
    {
        var policy = action.Policy;
        var retry = policy.EffectiveRetry;
        var claims = NormalizeWorkflowClaims(policy.Resources);
        WorkflowStepResult? lastFailure = null;
        Exception? lastException = null;
        var attempts = 0;

        for (var attempt = 1; attempt <= retry.MaxAttempts; attempt++)
        {
            attempts = attempt;

            if (control.IsAbortRequested)
                return new NodeResultCore(WorkflowNodeStatus.Aborted, attempts, "Abort requested.");

            if (control.IsStopRequested)
                return new NodeResultCore(WorkflowNodeStatus.Stopped, attempts, "Stop requested before action start.");

            var precondition = policy.Precondition;
            if (precondition is not null)
            {
                bool allowed;
                try
                {
                    allowed = await precondition(context, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (control.IsAbortRequested)
                {
                    return new NodeResultCore(WorkflowNodeStatus.Aborted, attempts, "Abort requested.");
                }
                catch (Exception ex)
                {
                    return new NodeResultCore(
                        WorkflowNodeStatus.Failed,
                        attempts,
                        $"Precondition for '{action.NodeId}' failed: {ex.Message}",
                        ex);
                }

                if (!allowed)
                {
                    return new NodeResultCore(
                        WorkflowNodeStatus.Failed,
                        attempts,
                        $"Precondition for '{action.NodeId}' was not satisfied.");
                }
            }

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                control.AbortToken);
            using var timeout = CreateCancellationTimer(policy.Timeout, attemptCts);

            var sideEffectMayHaveStarted = false;
            WorkflowStepResult? stepResult = null;

            try
            {
                using var resourceWaitCts = CancellationTokenSource.CreateLinkedTokenSource(
                    attemptCts.Token,
                    control.StopToken);

                var resourceWaitStarted = _timeProvider.GetTimestamp();
                await using var lease = await _resourceArbiter
                    .AcquireAsync(claims, resourceWaitCts.Token)
                    .ConfigureAwait(false);

                WorkflowExecutionTelemetry.ResourceWaitSeconds.Record(
                    _timeProvider.GetElapsedTime(resourceWaitStarted).TotalSeconds,
                    WorkflowExecutionTelemetry.ResourceTags(action));

                if (control.IsAbortRequested)
                    return new NodeResultCore(WorkflowNodeStatus.Aborted, attempts, "Abort requested.");

                if (control.IsStopRequested)
                    return new NodeResultCore(WorkflowNodeStatus.Stopped, attempts, "Stop requested before action dispatch.");

                sideEffectMayHaveStarted = policy.SideEffecting;
                stepResult = await action.Execute(context, attemptCts.Token).ConfigureAwait(false);

                if (stepResult.Status is WorkflowStepStatus.Succeeded or WorkflowStepStatus.Skipped)
                {
                    if (policy.CompletionCondition is not null)
                    {
                        var completed = await policy.CompletionCondition(
                            context,
                            stepResult,
                            attemptCts.Token).ConfigureAwait(false);

                        if (!completed)
                        {
                            if (policy.SideEffecting)
                            {
                                return new NodeResultCore(
                                    WorkflowNodeStatus.RecoveryRequired,
                                    attempts,
                                    $"Completion condition for side-effecting action '{action.NodeId}' was not satisfied; reconcile physical state before continuing.");
                            }

                            stepResult = WorkflowStepResult.Failure(
                                $"Completion condition for '{action.NodeId}' was not satisfied.");
                        }
                    }

                    if (stepResult.Status is WorkflowStepStatus.Succeeded or WorkflowStepStatus.Skipped)
                    {
                        return new NodeResultCore(
                            WorkflowNodeStatus.Succeeded,
                            attempts,
                            stepResult.Message);
                    }
                }

                if (stepResult.Status == WorkflowStepStatus.Cancelled)
                {
                    if (control.IsAbortRequested)
                        return new NodeResultCore(WorkflowNodeStatus.Aborted, attempts, stepResult.Message);

                    return new NodeResultCore(
                        WorkflowNodeStatus.Failed,
                        attempts,
                        stepResult.Message ?? $"Action '{action.NodeId}' returned Cancelled without an execution abort.",
                        stepResult.Exception);
                }

                lastFailure = stepResult;
                lastException = stepResult.Exception;
            }
            catch (WorkflowPhysicalOutcomeUnknownException ex)
            {
                return new NodeResultCore(
                    WorkflowNodeStatus.RecoveryRequired,
                    attempts,
                    ex.Message,
                    ex);
            }
            catch (OperationCanceledException ex) when (control.IsAbortRequested)
            {
                if (policy.SideEffecting && sideEffectMayHaveStarted)
                {
                    return new NodeResultCore(
                        WorkflowNodeStatus.RecoveryRequired,
                        attempts,
                        $"Action '{action.NodeId}' was aborted after its side-effect boundary; physical outcome must be reconciled.",
                        ex);
                }

                return new NodeResultCore(
                    WorkflowNodeStatus.Aborted,
                    attempts,
                    "Abort requested.",
                    ex);
            }
            catch (OperationCanceledException ex) when (timeout?.TimedOut == true)
            {
                if (policy.SideEffecting && sideEffectMayHaveStarted)
                {
                    return new NodeResultCore(
                        WorkflowNodeStatus.RecoveryRequired,
                        attempts,
                        $"Action '{action.NodeId}' timed out after its side-effect boundary; physical outcome must be reconciled.",
                        ex);
                }

                lastFailure = WorkflowStepResult.Failure(
                    $"Action '{action.NodeId}' timed out.",
                    ex);
                lastException = ex;
            }
            catch (OperationCanceledException ex) when (control.IsStopRequested)
            {
                return new NodeResultCore(
                    WorkflowNodeStatus.Stopped,
                    attempts,
                    "Stop requested before the action crossed its side-effect boundary.",
                    ex);
            }
            catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
            {
                if (policy.SideEffecting && sideEffectMayHaveStarted)
                {
                    return new NodeResultCore(
                        WorkflowNodeStatus.RecoveryRequired,
                        attempts,
                        $"Action '{action.NodeId}' was cancelled after its side-effect boundary; physical outcome must be reconciled.",
                        ex);
                }

                return new NodeResultCore(
                    WorkflowNodeStatus.Stopped,
                    attempts,
                    $"Action '{action.NodeId}' was cancelled by its owning execution scope.",
                    ex);
            }
            catch (Exception ex)
            {
                if (policy.SideEffecting && sideEffectMayHaveStarted)
                {
                    return new NodeResultCore(
                        WorkflowNodeStatus.RecoveryRequired,
                        attempts,
                        $"Side-effecting action '{action.NodeId}' faulted after dispatch; physical outcome must be reconciled: {ex.Message}",
                        ex);
                }

                lastFailure = WorkflowStepResult.Failure(
                    $"Action '{action.NodeId}' faulted: {ex.Message}",
                    ex);
                lastException = ex;
            }

            lastFailure ??= WorkflowStepResult.Failure(
                $"Action '{action.NodeId}' failed without a result.");
            lastException ??= lastFailure.Exception;

            var canRetry =
                attempt < retry.MaxAttempts &&
                (policy.RetryPredicate?.Invoke(lastFailure) ?? !policy.SideEffecting);

            if (!canRetry)
                break;

            WorkflowExecutionTelemetry.Retries.Add(1);

            using var retryCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                control.AbortToken,
                control.StopToken);

            try
            {
                await DelayAsync(retry.Delay, retryCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (control.IsAbortRequested)
            {
                return new NodeResultCore(WorkflowNodeStatus.Aborted, attempts, "Abort requested during retry delay.");
            }
            catch (OperationCanceledException) when (control.IsStopRequested)
            {
                return new NodeResultCore(WorkflowNodeStatus.Stopped, attempts, "Stop requested during retry delay.");
            }
        }

        if (policy.Compensation is not null)
        {
            try
            {
                var compensation = await policy.Compensation(
                    context,
                    cancellationToken).ConfigureAwait(false);

                if (compensation.Status is WorkflowStepStatus.Failed or WorkflowStepStatus.Cancelled)
                {
                    return new NodeResultCore(
                        WorkflowNodeStatus.RecoveryRequired,
                        attempts,
                        $"Compensation for '{action.NodeId}' failed: {compensation.Message}",
                        compensation.Exception);
                }
            }
            catch (Exception ex)
            {
                return new NodeResultCore(
                    WorkflowNodeStatus.RecoveryRequired,
                    attempts,
                    $"Compensation for '{action.NodeId}' faulted: {ex.Message}",
                    ex);
            }
        }

        return new NodeResultCore(
            WorkflowNodeStatus.Failed,
            attempts,
            lastFailure?.Message ?? $"Action '{action.NodeId}' failed.",
            lastException);
    }
}
