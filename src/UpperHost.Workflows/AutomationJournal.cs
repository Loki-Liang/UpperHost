using System.Threading.Channels;

namespace UpperHost.Workflows;

public enum AutomationJournalEventType
{
    ExecutionStarted,
    StationTransition,
    ControlRequested,
    StepStarted,
    ResourceAcquired,
    ResourceReleased,
    StepCompleted,
    CheckpointReached,
    RecoveryStarted,
    RecoveryCompleted,
    ExecutionCompleted
}

public enum AutomationJournalFailurePolicy
{
    FailExecution,
    DegradeAndAlert
}

public sealed record AutomationJournalEvent(
    string ExecutionId,
    long Sequence,
    DateTimeOffset Timestamp,
    AutomationJournalEventType EventType,
    string? NodeId = null,
    AutomationStepStatus? StepStatus = null,
    string? CommandExecutionId = null,
    AutomationStationState? StationFrom = null,
    AutomationStationState? StationTo = null,
    string? Detail = null);

public interface IAutomationExecutionJournal
{
    ValueTask AppendAsync(
        AutomationJournalEvent journalEvent,
        CancellationToken cancellationToken = default);
}

public interface IAutomationJournalSink
{
    ValueTask WriteAsync(
        AutomationJournalEvent journalEvent,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryAutomationJournalSink : IAutomationJournalSink
{
    private readonly object _gate = new();
    private readonly List<AutomationJournalEvent> _events = [];

    public ValueTask WriteAsync(
        AutomationJournalEvent journalEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(journalEvent);

        lock (_gate)
            _events.Add(journalEvent);

        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<AutomationJournalEvent> Snapshot()
    {
        lock (_gate)
            return _events.ToArray();
    }
}

public sealed record BoundedAutomationExecutionJournalOptions(
    int Capacity = 256,
    AutomationJournalFailurePolicy FailurePolicy = AutomationJournalFailurePolicy.FailExecution)
{
    internal void Validate()
    {
        if (Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(Capacity));
    }
}

public sealed class BoundedAutomationExecutionJournal :
    IAutomationExecutionJournal,
    IAsyncDisposable
{
    private sealed record PendingWrite(
        AutomationJournalEvent Event,
        TaskCompletionSource<bool> Completion);

    private readonly IAutomationJournalSink _sink;
    private readonly BoundedAutomationExecutionJournalOptions _options;
    private readonly Channel<PendingWrite> _channel;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;
    private Exception? _terminalError;
    private int _state;
    private int _degraded;

    public BoundedAutomationExecutionJournal(
        IAutomationJournalSink sink,
        BoundedAutomationExecutionJournalOptions? options = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = options ?? new BoundedAutomationExecutionJournalOptions();
        _options.Validate();

        _channel = Channel.CreateBounded<PendingWrite>(
            new BoundedChannelOptions(_options.Capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
    }

    public bool IsRunning => Volatile.Read(ref _state) == 1;
    public bool IsDegraded => Volatile.Read(ref _degraded) != 0;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("Automation execution journal can only be started once.");

        _worker = RunAsync(_shutdown.Token);
        return Task.CompletedTask;
    }

    public async ValueTask AppendAsync(
        AutomationJournalEvent journalEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(journalEvent);

        var terminal = Volatile.Read(ref _terminalError);
        if (terminal is not null && _options.FailurePolicy == AutomationJournalFailurePolicy.FailExecution)
            throw new InvalidOperationException("Automation journal is faulted.", terminal);

        if (!IsRunning)
            throw new InvalidOperationException("Automation execution journal is not running.");

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingWrite(journalEvent, completion);

        await _channel.Writer.WriteAsync(pending, cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var previous = Interlocked.CompareExchange(ref _state, 2, 1);
        if (previous == 0)
        {
            Interlocked.Exchange(ref _state, 3);
            return;
        }

        if (previous is 2 or 3)
        {
            if (_worker is not null)
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        _channel.Writer.TryComplete();

        try
        {
            if (_worker is not null)
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _shutdown.Cancel();
            if (_worker is not null)
                await _worker.ConfigureAwait(false);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _state, 3);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (IsRunning)
            await StopAsync().ConfigureAwait(false);

        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await foreach (var pending in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await _sink.WriteAsync(pending.Event, cancellationToken).ConfigureAwait(false);
                    pending.Completion.TrySetResult(true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    pending.Completion.TrySetCanceled(cancellationToken);
                    throw;
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _terminalError, ex);

                    if (_options.FailurePolicy == AutomationJournalFailurePolicy.DegradeAndAlert)
                    {
                        Interlocked.Exchange(ref _degraded, 1);
                        pending.Completion.TrySetResult(true);
                        continue;
                    }

                    failure = ex;
                    pending.Completion.TrySetException(
                        new InvalidOperationException("Automation journal sink failed.", ex));
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failure = new OperationCanceledException("Automation journal stopped.", cancellationToken);
        }
        finally
        {
            while (_channel.Reader.TryRead(out var pending))
            {
                if (failure is OperationCanceledException cancelled)
                    pending.Completion.TrySetCanceled(cancelled.CancellationToken);
                else if (failure is not null)
                    pending.Completion.TrySetException(failure);
                else
                    pending.Completion.TrySetException(
                        new InvalidOperationException("Automation journal stopped before the event was written."));
            }
        }
    }
}

public sealed class NullAutomationExecutionJournal : IAutomationExecutionJournal
{
    public static NullAutomationExecutionJournal Instance { get; } = new();

    private NullAutomationExecutionJournal()
    {
    }

    public ValueTask AppendAsync(
        AutomationJournalEvent journalEvent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
