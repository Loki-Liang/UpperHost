using System.Text.Json;
using UpperHost.Sample.DataAcquisition.Acquisition;

namespace UpperHost.Sample.DataAcquisition.Consumers;

public sealed class JsonLinesStorageConsumer
{
    public JsonLinesStorageConsumer(string path) => Path = path ?? throw new ArgumentNullException(nameof(path));

    public string Path { get; }

    public async Task<int> RunAsync(
        IAsyncEnumerable<SampleFrame> frames,
        CancellationToken cancellationToken = default)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await using var stream = new FileStream(
            Path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16 * 1024,
            useAsync: true);
        await using var writer = new StreamWriter(stream);

        var count = 0;
        await foreach (var frame in frames.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(frame)).ConfigureAwait(false);
            count++;
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }
}
