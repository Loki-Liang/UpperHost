using System.Text.Json;
using OpenDeviceStudio.Sample.DataAcquisition.Acquisition;

namespace OpenDeviceStudio.Sample.DataAcquisition.Consumers;

public sealed class JsonLinesStorageConsumer : IAsyncDisposable
{
    private FileStream? _stream;
    private StreamWriter? _writer;
    private int _count;
    private int _prepared;
    private int _disposed;

    public JsonLinesStorageConsumer(string path) =>
        Path = path ?? throw new ArgumentNullException(nameof(path));

    public string Path { get; }

    public int Count => Volatile.Read(ref _count);

    public ValueTask PrepareAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();

        if (Interlocked.Exchange(ref _prepared, 1) != 0)
            return ValueTask.CompletedTask;

        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        _stream = new FileStream(
            Path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16 * 1024,
            useAsync: true);
        _writer = new StreamWriter(_stream);
        return ValueTask.CompletedTask;
    }

    public async ValueTask ConsumeAsync(
        SampleFrame frame,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _prepared) == 0 || _writer is null)
            throw new InvalidOperationException("Storage consumer must be prepared before consuming frames.");

        var json = JsonSerializer.Serialize(frame);
        await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _count);
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_writer is not null)
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RunAsync(
        IAsyncEnumerable<SampleFrame> frames,
        CancellationToken cancellationToken = default)
    {
        await PrepareAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (var frame in frames.WithCancellation(cancellationToken).ConfigureAwait(false))
                await ConsumeAsync(frame, cancellationToken).ConfigureAwait(false);

            await CompleteAsync(cancellationToken).ConfigureAwait(false);
            return Count;
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        if (_writer is not null)
            await _writer.DisposeAsync().ConfigureAwait(false);
        else if (_stream is not null)
            await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(JsonLinesStorageConsumer));
    }
}
