using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using UpperHost.Acquisition;

namespace UpperHost.Tests;

public sealed class AcquisitionSessionTests
{
    [Fact]
    public async Task Raw_acceptance_precedes_processing_and_closed_session_rejects_late_blocks()
    {
        var calls = new List<string>();
        var raw = new RecordingRawSink(calls);
        var processing = new RecordingProcessingSink(calls);
        RawFirstAcquisitionIngress<int>? ingress = null;
        RecordingSource? source = null;

        source = new RecordingSource("source-a")
        {
            StartHook = async token =>
            {
                ingress = source!.Context!.CreateRawFirstIngress(raw, processing);
                await ingress.PublishAsync(42, token);
            }
        };

        await using var manager = new AcquisitionSessionManager();
        await using var session = manager.CreateSession(Live([source]));

        await session.StartAsync();

        Assert.Equal(["raw:42", "processing:42"], calls);

        raw.Accept = false;
        var rejected = await Assert.ThrowsAsync<AcquisitionRawRejectedException>(
            async () => await ingress!.PublishAsync(43));

        Assert.Contains("rejected", rejected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("processing:43", calls);

        raw.Accept = true;
        var result = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AcquisitionSessionState.Completed, result.TerminalState);

        await Assert.ThrowsAsync<AcquisitionIngressClosedException>(
            async () => await ingress!.PublishAsync(44));

        Assert.DoesNotContain("raw:44", calls);
        Assert.DoesNotContain("processing:44", calls);
        Assert.Equal(1, session.GetSnapshot().RejectedLateIngress);
    }

    [Fact]
    public async Task Required_readiness_barrier_finishes_before_source_start()
    {
        var prepareGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recorder = new RecordingComponent("raw", AcquisitionComponentKind.RawRecorder)
        {
            PrepareHook = token => new ValueTask(prepareGate.Task.WaitAsync(token))
        };
        var source = new RecordingSource("source-a");

        await using var manager = new AcquisitionSessionManager();
        await using var session = manager.CreateSession(Live(
            [source],
            [new AcquisitionRequiredComponentRegistration(recorder)]));

        var start = session.StartAsync();
        await recorder.PrepareEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, source.StartCount);
        Assert.Equal(AcquisitionSessionState.Preparing, session.State);

        prepareGate.TrySetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, source.StartCount);
        Assert.Equal(AcquisitionSessionState.Running, session.State);
        Assert.Equal(2, session.GetSnapshot().RequiredReady);

        var result = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AcquisitionSessionState.Completed, result.TerminalState);
    }

    [Fact]
    public async Task Startup_failure_rolls_back_registered_resources_in_lifo_order()
    {
        var rollbackOrder = new List<string>();
        var processing = new RecordingComponent("processing", AcquisitionComponentKind.Processing)
        {
            StopHook = _ =>
            {
                rollbackOrder.Add("processing");
                return ValueTask.CompletedTask;
            }
        };
        var router = new RecordingComponent("router", AcquisitionComponentKind.Router)
        {
            StopHook = _ =>
            {
                rollbackOrder.Add("router");
                return ValueTask.CompletedTask;
            }
        };
        var raw = new RecordingComponent("raw", AcquisitionComponentKind.RawRecorder)
        {
            PrepareHook = _ => throw new InvalidOperationException("raw preflight failed"),
            StopHook = _ =>
            {
                rollbackOrder.Add("raw");
                return ValueTask.CompletedTask;
            }
        };
        var source = new RecordingSource("source-a");

        await using var manager = new AcquisitionSessionManager();
        await using var session = manager.CreateSession(Live(
            [source],
            [
                new AcquisitionRequiredComponentRegistration(processing),
                new AcquisitionRequiredComponentRegistration(router),
                new AcquisitionRequiredComponentRegistration(raw)
            ]));

        await session.StartAsync();
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AcquisitionSessionState.Faulted, result.TerminalState);
        Assert.Equal(AcquisitionFaultCategory.Preparation, result.RootFault?.Category);
        Assert.Equal(["raw", "router", "processing"], rollbackOrder);
        Assert.Equal(0, source.StartCount);
        Assert.Equal(1, raw.DisposeCount);
        Assert.Equal(1, router.DisposeCount);
        Assert.Equal(1, processing.DisposeCount);
    }

    [Fact]
    public async Task Partial_source_start_callback_is_preserved_then_late_callback_is_rejected()
    {
        var calls = new List<string>();
        var rawSink = new RecordingRawSink(calls);
        var processingSink = new RecordingProcessingSink(calls);
        var recorder = new RecordingComponent("raw", AcquisitionComponentKind.RawRecorder);
        RawFirstAcquisitionIngress<int>? ingress = null;
        RecordingSource? source = null;

        source = new RecordingSource("source-a")
        {
            StartHook = async token =>
            {
                ingress = source!.Context!.CreateRawFirstIngress(rawSink, processingSink);
                await ingress.PublishAsync(1, token);
                throw new InvalidOperationException("partial start");
            }
        };

        await using var manager = new AcquisitionSessionManager();
        await using var session = manager.CreateSession(Live(
            [source],
            [new AcquisitionRequiredComponentRegistration(recorder)]));

        await session.StartAsync();
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AcquisitionSessionState.Faulted, result.TerminalState);
        Assert.Equal(AcquisitionFaultCategory.Source, result.RootFault?.Category);
        Assert.Equal(["raw:1", "processing:1"], calls);
        Assert.Equal(1, source.StartCount);
        Assert.Equal(1, source.StopCount);
        Assert.Equal(1, recorder.StopCount);
        Assert.Equal(1, recorder.FinalizeCount);
        Assert.Equal(1, recorder.DisposeCount);

        await Assert.ThrowsAsync<AcquisitionIngressClosedException>(
            async () => await ingress!.PublishAsync(2));
        Assert.Equal(["raw:1", "processing:1"], calls);
    }

    [Theory]
    [InlineData(AcquisitionComponentKind.RawRecorder, AcquisitionFaultCategory.RawIntegrity)]
    [InlineData(AcquisitionComponentKind.Processing, AcquisitionFaultCategory.Processing)]
    [InlineData(AcquisitionComponentKind.Router, AcquisitionFaultCategory.RequiredBranch)]
    public async Task Required_component_fault_matrix_converges_with_structured_root_fault(
        AcquisitionComponentKind kind,
        AcquisitionFaultCategory category)
    {
        var required = new RecordingComponent($"required-{kind}", kind);
        var source = new RecordingSource("source-a");

        await using var manager = new AcquisitionSessionManager();
        await using var session = manager.CreateSession(Live(
            [source],
            [new AcquisitionRequiredComponentRegistration(required)]));

        await session.StartAsync();

        Assert.True(required.Context!.TryReportFault(
            category,
            new InvalidOperationException($"{kind} failed"),
            $"{kind} failed"));

        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AcquisitionSessionState.Faulted, result.TerminalState);
        Assert.Equal(category, result.RootFault?.Category);
        Assert.Equal(required.ComponentId, result.RootFault?.ComponentId);
        Assert.Equal(1, source.StopCount);
        Assert.Equal(1, required.StopCount);
        Assert.Equal(1, required.FinalizeCount);
        Assert.Contains(result.Components, item =>
            item.ComponentId == required.ComponentId &&
            item.State == AcquisitionComponentRuntimeState.Faulted);
    }

    [Fact]
    public async Task First_required_fault_wins_and_secondary_faults_are_bounded()
    {
        var required = new RecordingComponent("processing", AcquisitionComponentKind.Processing);
        var source = new RecordingSource("source-a");
        var options = new AcquisitionSessionOptions(SecondaryFaultCapacity: 1);

        await using var manager = new AcquisitionSessionManager();
        await using var session = manager.CreateSession(Live(
            [source],
            [new AcquisitionRequiredComponentRegistration(required)],
            options: options));

        await session.StartAsync();

        Assert.True(required.Context!.TryReportFault(
            AcquisitionFaultCategory.Processing,
            new InvalidOperationException("root"),
            "root"));
        Assert.True(required.Context.TryReportFault(
            AcquisitionFaultCategory.Processing,
            new InvalidOperationException("secondary-1"),
            "secondary-1"));
        Assert.True(required.Context.TryReportFault(
            AcquisitionFaultCategory.Processing,
            new InvalidOperationException("secondary-2"),
            "secondary-2"));

        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AcquisitionSessionState.Faulted, result.TerminalState);
        Assert.Equal("root", result.RootFault?.Message);
        Assert.Single(result.SecondaryFaults);
        Assert.True(result.DroppedSecondaryFaults >= 1);
        Assert.Equal(1, required.StopCount);
        Assert.Equal(1, required.FinalizeCount);
    }

    [Fact]
    public async Task Optional_fault_is_isolated_and_component_can_be_reattached()
    {
        var source = new RecordingSource("source-a");
        var optional = new RecordingOptional("presentation", AcquisitionComponentKind.OptionalPresentation);

        await using var manager = new AcquisitionSessionManager();
        await using var session = manager.CreateSession(Live(
            [source],
            optional:
            [
                new AcquisitionOptionalComponentRegistration(optional)
            ]));

        await session.StartAsync();
        Assert.Equal(1, optional.AttachCount);

        Assert.True(optional.Context!.TryReportFault(
            AcquisitionFaultCategory.OptionalComponent,
            new InvalidOperationException("render failed"),
            "render failed"));

        await WaitUntilAsync(() => optional.DetachCount == 1);

        Assert.Equal(AcquisitionSessionState.Running, session.State);
        Assert.True(await session.AttachOptionalAsync(optional.ComponentId));
        Assert.Equal(2, optional.AttachCount);

        var result = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AcquisitionSessionState.Completed, result.TerminalState);
        Assert.Null(result.RootFault);
        Assert.Contains(result.SecondaryFaults, fault => fault.ComponentId == optional.ComponentId);
    }

    [Fact]
    public async Task Optional_fault_can_escalate_only_when_frozen_definition_requests_it()
    {
        var source = new RecordingSource("source-a");
        var optional = new RecordingOptional("critical-optional", AcquisitionComponentKind.OptionalAlgorithm);

        await using var manager = new AcquisitionSessionManager();
        await using var session = manager.CreateSession(Live(
            [source],
            optional:
            [
                new AcquisitionOptionalComponentRegistration(optional, EscalateFault: true)
            ]));

        await session.StartAsync();

        Assert.True(optional.Context!.TryReportFault(
            AcquisitionFaultCategory.OptionalComponent,
            new InvalidOperationException("configured escalation"),
            "configured escalation"));

        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AcquisitionSessionState.Faulted, result.TerminalState);
        Assert.Equal(AcquisitionFaultCategory.OptionalComponent, result.RootFault?.Category);
        Assert.Equal(optional.ComponentId, result.RootFault?.ComponentId);
        Assert.Contains(result.Components, item =>
            item.ComponentId == optional.ComponentId &&
            item.State == AcquisitionComponentRuntimeState.Faulted);
    }

    [Fact]
    public async Task Finalize_failure_prevents_completed_terminal_state()
    {
        var recorder = new RecordingComponent("raw", AcquisitionComponentKind.RawRecorder)
        {
            FinalizeHook = _ => throw new InvalidOperationException("manifest finalize failed")
        };
        var source = new RecordingSource("source-a");

        await using var manager = new AcquisitionSessionManager();
        await using var session = manager.CreateSession(Live(
            [source],
            [new AcquisitionRequiredComponentRegistration(recorder)]));

        await session.StartAsync();
        var result = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AcquisitionSessionState.Faulted, result.TerminalState);
        Assert.Equal(AcquisitionFaultCategory.Finalization, result.RootFault?.Category);
        Assert.Contains("finalization", result.RootFault?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Repeated_stop_and_abort_share_one_terminal_convergence()
    {
        var required = new RecordingComponent("processing", AcquisitionComponentKind.Processing);
        var source = new RecordingSource("source-a");

        await using var manager = new AcquisitionSessionManager();
        await using var session = manager.CreateSession(Live(
            [source],
            [new AcquisitionRequiredComponentRegistration(required)]));

        await session.StartAsync();

        var first = session.StopAsync();
        var second = session.StopAsync();
        var third = session.AbortAsync();

        var results = await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.All(results, result => Assert.Equal(results[0].TerminalState, result.TerminalState));
        Assert.True(source.StopCount <= 1);
        Assert.True(source.AbortCount <= 1);
        Assert.True(required.StopCount <= 1);
        Assert.True(required.FinalizeCount <= 1);
        Assert.True(required.AbortCount <= 1);
    }

    [Fact]
    public async Task Concurrent_stop_required_fault_and_abort_share_one_convergence_and_preserve_root_fault()
    {
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new RecordingSource("source-a")
        {
            StopHook = async token =>
            {
                stopEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };
        var processing = new RecordingComponent("processing", AcquisitionComponentKind.Processing);

        await using var manager = new AcquisitionSessionManager();
        await using var session = manager.CreateSession(Live(
            [source],
            [new AcquisitionRequiredComponentRegistration(processing)]));

        await session.StartAsync();

        var stopping = session.StopAsync();
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(processing.Context!.TryReportFault(
            AcquisitionFaultCategory.Processing,
            new InvalidOperationException("processing root"),
            "processing root"));

        var aborting = session.AbortAsync();
        var results = await Task.WhenAll(stopping, aborting).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.All(results, result => Assert.Equal(AcquisitionSessionState.Aborted, result.TerminalState));
        Assert.All(results, result => Assert.Equal("processing root", result.RootFault?.Message));
        Assert.Equal(1, source.StopCount);
        Assert.Equal(1, source.AbortCount);
        Assert.True(processing.AbortCount <= 1);
    }

    [Fact]
    public async Task Abort_preempts_an_inflight_graceful_stop()
    {
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new RecordingSource("source-a")
        {
            StopHook = async token =>
            {
                stopEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };

        await using var manager = new AcquisitionSessionManager();
        await using var session = manager.CreateSession(Live([source]));

        await session.StartAsync();

        var stopping = session.StopAsync();
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var aborting = session.AbortAsync();
        var results = await Task.WhenAll(stopping, aborting).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.All(results, result => Assert.Equal(AcquisitionSessionState.Aborted, result.TerminalState));
        Assert.Equal(1, source.StopCount);
        Assert.Equal(1, source.AbortCount);
    }

    [Fact]
    public async Task Multi_source_isolation_closes_only_failed_source_ingress_and_keeps_healthy_source_live()
    {
        var callsA = new List<string>();
        var callsB = new List<string>();
        var rawA = new RecordingRawSink(callsA);
        var rawB = new RecordingRawSink(callsB);
        var processingA = new RecordingProcessingSink(callsA);
        var processingB = new RecordingProcessingSink(callsB);
        RawFirstAcquisitionIngress<int>? ingressA = null;
        RawFirstAcquisitionIngress<int>? ingressB = null;

        RecordingSource? sourceA = null;
        RecordingSource? sourceB = null;
        sourceA = new RecordingSource("source-a")
        {
            StartHook = _ =>
            {
                ingressA = sourceA!.Context!.CreateRawFirstIngress(rawA, processingA);
                return ValueTask.CompletedTask;
            }
        };
        sourceB = new RecordingSource("source-b", connectionEpoch: 7)
        {
            StartHook = _ =>
            {
                ingressB = sourceB!.Context!.CreateRawFirstIngress(rawB, processingB);
                return ValueTask.CompletedTask;
            }
        };

        var observer = new RecordingIsolationObserver();
        var options = new AcquisitionSessionOptions(
            MultiSourceFailurePolicy: AcquisitionMultiSourceFailurePolicy.IsolateFailedSource);

        await using var manager = new AcquisitionSessionManager();
        await using var session = manager.CreateSession(new AcquisitionSessionDefinition(
            AcquisitionSessionMode.LiveAcquisition,
            [sourceA, sourceB],
            sourceIsolationObservers: [observer],
            options: options));

        await session.StartAsync();
        await ingressA!.PublishAsync(1);
        await ingressB!.PublishAsync(1);

        Assert.True(sourceB.Context!.TryReportFault(
            AcquisitionFaultCategory.Source,
            new IOException("link down"),
            "link down"));

        var isolation = await observer.Observed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("source-b", isolation.SourceId);
        Assert.Equal(7, isolation.ConnectionEpoch);
        Assert.Equal(session.ProcessingEpoch, isolation.ProcessingEpoch);
        Assert.Equal(AcquisitionSessionState.Running, session.State);
        Assert.Equal(0, sourceA.StopCount);
        Assert.Equal(1, sourceB.StopCount);

        await Assert.ThrowsAsync<AcquisitionIngressClosedException>(
            async () => await ingressB.PublishAsync(2));
        await ingressA.PublishAsync(2);

        Assert.Equal(["raw:1", "processing:1", "raw:2", "processing:2"], callsA);
        Assert.Equal(["raw:1", "processing:1"], callsB);
        Assert.True(session.GetSnapshot().IngressAccepting);
        Assert.Equal(1, session.GetSnapshot().RejectedLateIngress);

        var result = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AcquisitionSessionState.Completed, result.TerminalState);
        Assert.Equal(1, result.RejectedLateIngress);
        Assert.Contains(result.Sources, item =>
            item.SourceId == "source-b" && item.State == AcquisitionComponentRuntimeState.Isolated);
    }

    [Fact]
    public async Task Multi_source_fail_whole_policy_stops_every_source()
    {
        var sourceA = new RecordingSource("source-a");
        var sourceB = new RecordingSource("source-b");

        await using var manager = new AcquisitionSessionManager();
        await using var session = manager.CreateSession(new AcquisitionSessionDefinition(
            AcquisitionSessionMode.LiveAcquisition,
            [sourceA, sourceB],
            options: new AcquisitionSessionOptions(
                MultiSourceFailurePolicy: AcquisitionMultiSourceFailurePolicy.FailWholeSession)));

        await session.StartAsync();

        Assert.True(sourceB.Context!.TryReportFault(
            AcquisitionFaultCategory.Source,
            new IOException("source-b failed"),
            "source-b failed"));

        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AcquisitionSessionState.Faulted, result.TerminalState);
        Assert.Equal("source-b", result.RootFault?.SourceId);
        Assert.Equal(1, sourceA.StopCount);
        Assert.Equal(1, sourceB.StopCount);
    }

    [Fact]
    public async Task Replay_requires_read_only_source_and_does_not_create_default_raw_recorder()
    {
        var mutableReplay = new RecordingSource("replay", isReplay: true, isReadOnly: false);
        Assert.Throws<ArgumentException>(() => new AcquisitionSessionDefinition(
            AcquisitionSessionMode.Replay,
            [mutableReplay],
            replaySourceArtifactId: "raw-001"));

        var replay = new RecordingSource("replay", isReplay: true, isReadOnly: true);
        var raw = new RecordingComponent("raw", AcquisitionComponentKind.RawRecorder);
        Assert.Throws<ArgumentException>(() => new AcquisitionSessionDefinition(
            AcquisitionSessionMode.Replay,
            [replay],
            requiredComponents: [new AcquisitionRequiredComponentRegistration(raw)],
            replaySourceArtifactId: "raw-001"));

        await using var manager = new AcquisitionSessionManager();
        await using var session = manager.CreateSession(new AcquisitionSessionDefinition(
            AcquisitionSessionMode.Replay,
            [replay],
            replaySourceArtifactId: "raw-001"));

        await session.StartAsync();
        var result = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(AcquisitionSessionState.Completed, result.TerminalState);
        Assert.Equal("raw-001", result.ReplaySourceArtifactId);
        Assert.False(string.IsNullOrWhiteSpace(result.ProcessingEpoch));
    }

    [Fact]
    public async Task Cancelling_start_request_aborts_preparing_but_not_a_running_session()
    {
        var prepareGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var component = new RecordingComponent("processing", AcquisitionComponentKind.Processing)
        {
            PrepareHook = token => new ValueTask(prepareGate.Task.WaitAsync(token))
        };
        var source = new RecordingSource("source-a");

        await using var manager = new AcquisitionSessionManager();

        using (var startCancellation = new CancellationTokenSource())
        await using (var cancelledSession = manager.CreateSession(Live(
            [source],
            [new AcquisitionRequiredComponentRegistration(component)])))
        {
            var start = cancelledSession.StartAsync(startCancellation.Token);
            await component.PrepareEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            startCancellation.Cancel();

            await start;
            var cancelled = await cancelledSession.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(AcquisitionSessionState.Aborted, cancelled.TerminalState);
        }

        using var runningToken = new CancellationTokenSource();
        var runningSource = new RecordingSource("source-running");
        await using var runningSession = manager.CreateSession(Live([runningSource]));

        await runningSession.StartAsync(runningToken.Token);
        runningToken.Cancel();

        await Task.Yield();
        Assert.Equal(AcquisitionSessionState.Running, runningSession.State);

        var completed = await runningSession.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AcquisitionSessionState.Completed, completed.TerminalState);
    }

    [Fact]
    public async Task Stop_deadline_escalates_to_abort_with_testable_time()
    {
        var time = new FakeTimeProvider();
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new RecordingSource("source-a")
        {
            StopHook = async token =>
            {
                stopEntered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        };

        await using var manager = new AcquisitionSessionManager(time);
        await using var session = manager.CreateSession(Live(
            [source],
            options: new AcquisitionSessionOptions(
                StopTimeout: TimeSpan.FromSeconds(5),
                AbortTimeout: TimeSpan.FromSeconds(2))));

        await session.StartAsync();
        var stopping = session.StopAsync();
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        time.Advance(TimeSpan.FromSeconds(6));

        var result = await stopping.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AcquisitionSessionState.Aborted, result.TerminalState);
        Assert.Equal(AcquisitionFaultCategory.ShutdownTimeout, result.RootFault?.Category);
        Assert.Equal(1, source.AbortCount);
    }

    [Fact]
    public async Task Concurrent_dispose_calls_share_one_abort_and_dispose_sequence()
    {
        await using var manager = new AcquisitionSessionManager();
        var source = new RecordingSource("source-a");
        var session = manager.CreateSession(Live([source]));

        await session.StartAsync();

        var first = session.DisposeAsync().AsTask();
        var second = session.DisposeAsync().AsTask();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));

        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AcquisitionSessionState.Aborted, result.TerminalState);
        Assert.Equal(1, source.AbortCount);
        Assert.Equal(1, source.DisposeCount);

        await session.DisposeAsync();
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task Session_level_short_soak_reaches_quiescence_without_raw_processing_loss()
    {
        const int sessionCount = 12;
        const int blocksPerSession = 64;

        await using var manager = new AcquisitionSessionManager();

        for (var cycle = 0; cycle < sessionCount; cycle++)
        {
            var raw = new CountingRawSink();
            var processing = new CountingProcessingSink();
            RecordingSource? source = null;

            source = new RecordingSource($"source-{cycle}")
            {
                StartHook = async token =>
                {
                    var ingress = source!.Context!.CreateRawFirstIngress(raw, processing);
                    for (var block = 0; block < blocksPerSession; block++)
                        await ingress.PublishAsync(block, token);
                }
            };

            await using var session = manager.CreateSession(Live([source]));
            await session.StartAsync();
            var result = await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(AcquisitionSessionState.Completed, result.TerminalState);
            Assert.Equal(blocksPerSession, raw.Count);
            Assert.Equal(blocksPerSession, processing.Count);
            Assert.Equal(0, result.RejectedLateIngress);
            Assert.Equal(1, source.DisposeCount);
            Assert.Equal(0, manager.ActiveSessionCount);
        }
    }

    [Fact]
    public async Task Host_owns_one_session_manager_and_stops_live_sessions()
    {
        var services = new ServiceCollection();
        services.AddUpperHostAcquisition();
        services.AddUpperHostAcquisition();

        await using var provider = services.BuildServiceProvider();
        var managers = provider.GetServices<AcquisitionSessionManager>().ToArray();
        var hosted = provider.GetServices<IHostedService>().OfType<object>().ToArray();

        Assert.Single(managers);
        Assert.Single(hosted);

        var manager = managers[0];
        var source = new RecordingSource("source-a");
        await using var session = manager.CreateSession(Live([source]));
        await session.StartAsync();

        var hostedService = provider.GetRequiredService<IEnumerable<IHostedService>>().Single();
        await hostedService.StopAsync(CancellationToken.None);

        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(AcquisitionSessionState.Completed, result.TerminalState);
        Assert.Equal(0, manager.ActiveSessionCount);
    }

    private static AcquisitionSessionDefinition Live(
        IReadOnlyList<IAcquisitionSource> sources,
        IReadOnlyList<AcquisitionRequiredComponentRegistration>? required = null,
        IReadOnlyList<AcquisitionOptionalComponentRegistration>? optional = null,
        AcquisitionSessionOptions? options = null) =>
        new(
            AcquisitionSessionMode.LiveAcquisition,
            sources,
            required,
            optional,
            options: options);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!predicate())
            await Task.Delay(1, timeout.Token);
    }

    private sealed class RecordingComponent(
        string componentId,
        AcquisitionComponentKind kind) : IAcquisitionSessionComponent
    {
        public string ComponentId { get; } = componentId;
        public AcquisitionComponentKind Kind { get; } = kind;
        public AcquisitionComponentContext? Context { get; private set; }
        public TaskCompletionSource PrepareEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CancellationToken, ValueTask>? PrepareHook { get; init; }
        public Func<CancellationToken, ValueTask>? StopHook { get; init; }
        public Func<CancellationToken, ValueTask>? FinalizeHook { get; init; }
        public Func<CancellationToken, ValueTask>? AbortHook { get; init; }
        public int StopCount { get; private set; }
        public int FinalizeCount { get; private set; }
        public int AbortCount { get; private set; }
        public int DisposeCount { get; private set; }

        public async ValueTask PrepareAsync(
            AcquisitionComponentContext context,
            CancellationToken cancellationToken = default)
        {
            Context = context;
            PrepareEntered.TrySetResult();
            if (PrepareHook is not null)
                await PrepareHook(cancellationToken);
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            if (StopHook is not null)
                await StopHook(cancellationToken);
        }

        public async ValueTask FinalizeAsync(CancellationToken cancellationToken = default)
        {
            FinalizeCount++;
            if (FinalizeHook is not null)
                await FinalizeHook(cancellationToken);
        }

        public async ValueTask AbortAsync(CancellationToken cancellationToken = default)
        {
            AbortCount++;
            if (AbortHook is not null)
                await AbortHook(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingSource : IAcquisitionSource
    {
        public RecordingSource(
            string sourceId,
            long connectionEpoch = 1,
            bool isReplay = false,
            bool isReadOnly = false)
        {
            SourceId = sourceId;
            ComponentId = $"source:{sourceId}";
            ConnectionEpoch = connectionEpoch;
            IsReplay = isReplay;
            IsReadOnly = isReadOnly;
        }

        public string ComponentId { get; }
        public string SourceId { get; }
        public long ConnectionEpoch { get; }
        public bool IsReplay { get; }
        public bool IsReadOnly { get; }
        public AcquisitionComponentKind Kind => AcquisitionComponentKind.Source;
        public AcquisitionComponentContext? Context { get; private set; }
        public Func<CancellationToken, ValueTask>? StartHook { get; init; }
        public Func<CancellationToken, ValueTask>? StopHook { get; init; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int FinalizeCount { get; private set; }
        public int AbortCount { get; private set; }
        public int DisposeCount { get; private set; }

        public ValueTask PrepareAsync(
            AcquisitionComponentContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Context = context;
            return ValueTask.CompletedTask;
        }

        public async ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            StartCount++;
            if (StartHook is not null)
                await StartHook(cancellationToken);
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            StopCount++;
            if (StopHook is not null)
                await StopHook(cancellationToken);
        }

        public ValueTask FinalizeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FinalizeCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask AbortAsync(CancellationToken cancellationToken = default)
        {
            AbortCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingOptional(
        string componentId,
        AcquisitionComponentKind kind) : IAcquisitionOptionalComponent
    {
        public string ComponentId { get; } = componentId;
        public AcquisitionComponentKind Kind { get; } = kind;
        public AcquisitionComponentContext? Context { get; private set; }
        public int AttachCount { get; private set; }
        public int DetachCount { get; private set; }
        public int AbortCount { get; private set; }

        public ValueTask AttachAsync(
            AcquisitionComponentContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Context = context;
            AttachCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DetachAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DetachCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask AbortAsync(CancellationToken cancellationToken = default)
        {
            AbortCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingIsolationObserver : IAcquisitionSourceIsolationObserver
    {
        public TaskCompletionSource<AcquisitionSourceIsolation> Observed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask SourceIsolatedAsync(
            AcquisitionSourceIsolation isolation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Observed.TrySetResult(isolation);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingRawSink : IAcquisitionRawSink<int>
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public ValueTask<AcquisitionRawAcceptance> AcceptAsync(
            int block,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(AcquisitionRawAcceptance.Success);
        }
    }

    private sealed class CountingProcessingSink : IAcquisitionProcessingSink<int>
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);

        public ValueTask HandoffAsync(
            int block,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingRawSink(List<string> calls) : IAcquisitionRawSink<int>
    {
        public bool Accept { get; set; } = true;

        public ValueTask<AcquisitionRawAcceptance> AcceptAsync(
            int block,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add($"raw:{block}");
            return ValueTask.FromResult(
                Accept
                    ? AcquisitionRawAcceptance.Success
                    : new AcquisitionRawAcceptance(false, "rejected"));
        }
    }

    private sealed class RecordingProcessingSink(List<string> calls) : IAcquisitionProcessingSink<int>
    {
        public ValueTask HandoffAsync(
            int block,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add($"processing:{block}");
            return ValueTask.CompletedTask;
        }
    }
}
