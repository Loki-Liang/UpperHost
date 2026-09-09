using UpperHost.Abstractions.Devices;
using UpperHost.Control.Parameters;

namespace UpperHost.Control.Recipes;

public sealed record RecipeParameterValue(
    string DeviceId,
    string ParameterKey,
    object? Value,
    bool VerifyReadback = true);

public sealed record RecipeDefinition(string Name, IReadOnlyList<RecipeParameterValue> Parameters)
{
    public static RecipeDefinition Create(string name, params RecipeParameterValue[] parameters) =>
        new(name, parameters);
}

public enum RecipeItemStatus
{
    Applied,
    Failed
}

public sealed record RecipeItemResult(
    RecipeParameterValue Parameter,
    RecipeItemStatus Status,
    ParameterWriteResult? WriteResult = null,
    string? Error = null,
    Exception? Exception = null);

public sealed record RecipeApplyResult(
    string Recipe,
    IReadOnlyList<RecipeItemResult> Items)
{
    public bool IsSuccess => Items.All(item => item.Status == RecipeItemStatus.Applied);
}

public sealed record RecipeApplyOptions(bool StopOnFailure = true, double NumericTolerance = 0.000001);

public sealed class RecipeApplier
{
    private readonly IDeviceRegistry _registry;

    public RecipeApplier(IDeviceRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public async Task<RecipeApplyResult> ApplyAsync(
        RecipeDefinition recipe,
        RecipeApplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        options ??= new RecipeApplyOptions();
        var results = new List<RecipeItemResult>(recipe.Parameters.Count);

        foreach (var item in recipe.Parameters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!_registry.TryGet(item.DeviceId, out var device) || device is null)
                    throw new KeyNotFoundException($"Device '{item.DeviceId}' was not found.");
                if (device is not IParameterProvider parameterProvider)
                    throw new InvalidOperationException($"Device '{item.DeviceId}' does not provide parameters.");

                var service = new ParameterReadbackService(parameterProvider);
                var write = await service.WriteAsync(
                    item.ParameterKey,
                    item.Value,
                    new ParameterWriteOptions(
                        VerifyReadback: item.VerifyReadback,
                        ThrowOnMismatch: item.VerifyReadback,
                        NumericTolerance: options.NumericTolerance),
                    cancellationToken).ConfigureAwait(false);

                results.Add(new RecipeItemResult(item, RecipeItemStatus.Applied, write));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new RecipeItemResult(item, RecipeItemStatus.Failed, Error: ex.Message, Exception: ex));
                if (options.StopOnFailure)
                    break;
            }
        }

        return new RecipeApplyResult(recipe.Name, results);
    }
}
