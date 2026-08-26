namespace UpperHost.Abstractions.Data;

public interface IDataSource<T>
{
    IAsyncEnumerable<T> ReadAsync(CancellationToken cancellationToken = default);
}

public interface IDataSink<in T>
{
    ValueTask WriteAsync(T item, CancellationToken cancellationToken = default);
}

public interface IDataProcessor<in TIn, TOut>
{
    ValueTask<TOut> ProcessAsync(TIn input, CancellationToken cancellationToken = default);
}

public sealed record TelemetryPoint(
    string Name,
    double Value,
    string? Unit,
    DateTimeOffset Timestamp,
    IReadOnlyDictionary<string, string>? Tags = null);
