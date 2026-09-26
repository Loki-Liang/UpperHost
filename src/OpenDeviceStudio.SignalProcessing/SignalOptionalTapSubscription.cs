using OpenDeviceStudio.Dataflow;

namespace OpenDeviceStudio.SignalProcessing;

public sealed class SignalOptionalTapSubscription<T> : IAsyncDisposable
    where T : unmanaged
{
    private readonly StreamRouter<SignalBlock<T>>.StreamBranchSubscription _inner;
    private int _disposed;

    internal SignalOptionalTapSubscription(
        string stageId,
        string tapId,
        StreamRouter<SignalBlock<T>>.StreamBranchSubscription inner)
    {
        StageId = stageId;
        TapId = tapId;
        _inner = inner;
    }

    public string StageId { get; }
    public string TapId { get; }
    public Task Completion => _inner.Completion;

    public StreamBranchSnapshot GetSnapshot() => _inner.GetSnapshot();

    public ValueTask DetachAsync(CancellationToken cancellationToken = default) =>
        _inner.DetachAsync(StreamCompletionMode.Cancel, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await DetachAsync().ConfigureAwait(false);
    }
}
