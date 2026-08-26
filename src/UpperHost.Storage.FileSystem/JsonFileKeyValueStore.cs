using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UpperHost.Abstractions.Storage;

namespace UpperHost.Storage.FileSystem;

public sealed class JsonFileKeyValueStore : IKeyValueStore
{
    private readonly string _root;
    private readonly JsonSerializerOptions _jsonOptions;

    public JsonFileKeyValueStore(string root, JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
        _jsonOptions = jsonOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
    }

    public async ValueTask<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var path = GetPath(key);
        if (!File.Exists(path))
            return default;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return await JsonSerializer.DeserializeAsync<T>(stream, _jsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        var path = GetPath(key);
        var tempPath = path + ".tmp";

        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await JsonSerializer.SerializeAsync(stream, value, _jsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, path, overwrite: true);
    }

    public ValueTask<bool> DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(key);
        if (!File.Exists(path))
            return ValueTask.FromResult(false);

        File.Delete(path);
        return ValueTask.FromResult(true);
    }

    private string GetPath(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_root, hash + ".json");
    }
}
