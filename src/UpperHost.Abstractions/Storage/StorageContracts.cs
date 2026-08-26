namespace UpperHost.Abstractions.Storage;

public interface IKeyValueStore
{
    ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default);
    ValueTask SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);
    ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public interface IAppendOnlyStore<in T>
{
    ValueTask AppendAsync(T item, CancellationToken cancellationToken = default);
}

public interface IBlobStore
{
    ValueTask<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);
    ValueTask WriteAsync(string key, Stream content, CancellationToken cancellationToken = default);
    ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default);
}
