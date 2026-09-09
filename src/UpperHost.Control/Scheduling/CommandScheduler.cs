using UpperHost.Control.Commands;

namespace UpperHost.Control.Scheduling;

public enum CommandPriority
{
    Critical = 0,
    High = 10,
    Normal = 20,
    Low = 30
}

public sealed class CommandScheduler<TCommand, TResult> : IAsyncDisposable
{
    private sealed record ScheduledCommand(
        TCommand Command,
        CommandExecutionOptions? Options,
        CancellationToken CancellationToken,
        TaskCompletionSource<CommandExecutionResult<TResult>> Completion);

    private readonly CommandRuntime<TCommand, TResult> _runtime;
    private readonly PriorityQueue<ScheduledCommand, (int Priority, long Sequence)> _queue = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private long _sequence;
    private bool _disposed;

    public CommandScheduler(CommandRuntime<TCommand, TResult> runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _worker = Task.Run(RunAsync);
    }

    public int PendingCount
    {
        get
        {
            lock (_gate)
                return _queue.Count;
        }
    }

    public Task<CommandExecutionResult<TResult>> EnqueueAsync(
        TCommand command,
        CommandPriority priority = CommandPriority.Normal,
        CommandExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<CommandExecutionResult<TResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var sequence = Interlocked.Increment(ref _sequence);
            _queue.Enqueue(
                new ScheduledCommand(command, options, cancellationToken, completion),
                ((int)priority, sequence));
        }

        _available.Release();
        return completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _shutdown.Cancel();
        _available.Release();

        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _shutdown.Dispose();
            _available.Dispose();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await _available.WaitAsync(_shutdown.Token).ConfigureAwait(false);

                ScheduledCommand? work = null;
                lock (_gate)
                {
                    if (_queue.Count > 0)
                        work = _queue.Dequeue();
                }

                if (work is null || work.Completion.Task.IsCompleted)
                    continue;

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    _shutdown.Token,
                    work.CancellationToken);

                try
                {
                    var result = await _runtime.ExecuteAsync(
                        work.Command,
                        work.Options,
                        linked.Token).ConfigureAwait(false);
                    work.Completion.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    work.Completion.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        finally
        {
            List<ScheduledCommand> abandoned = [];
            lock (_gate)
            {
                while (_queue.Count > 0)
                    abandoned.Add(_queue.Dequeue());
            }

            foreach (var work in abandoned)
                work.Completion.TrySetException(new ObjectDisposedException(GetType().Name));
        }
    }
}
