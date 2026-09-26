using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace UpperHost.Workflows;

public sealed record AutomationRecipeSnapshot(
    string RecipeId,
    string Version,
    string SchemaVersion,
    string CanonicalJson,
    string Hash)
{
    public static AutomationRecipeSnapshot Create<T>(
        string recipeId,
        string version,
        T values,
        string schemaVersion = "1",
        JsonSerializerOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(recipeId))
            throw new ArgumentException("RecipeId cannot be empty.", nameof(recipeId));
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Recipe version cannot be empty.", nameof(version));
        if (string.IsNullOrWhiteSpace(schemaVersion))
            throw new ArgumentException("Recipe schema version cannot be empty.", nameof(schemaVersion));

        var json = JsonSerializer.Serialize(values, options);
        using var document = JsonDocument.Parse(json);
        var canonical = Canonicalize(document.RootElement);
        var hashInput = $"{schemaVersion}\n{canonical}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant();

        return new AutomationRecipeSnapshot(recipeId, version, schemaVersion, canonical, hash);
    }

    public T? Deserialize<T>(JsonSerializerOptions? options = null) =>
        JsonSerializer.Deserialize<T>(CanonicalJson, options);

    private static string Canonicalize(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteCanonical(writer, element);

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(static property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}

public abstract class AutomationNode
{
    protected AutomationNode(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Automation node id cannot be empty.", nameof(id));
        Id = id;
    }

    public string Id { get; }
}

public sealed class AutomationActionNode : AutomationNode
{
    public AutomationActionNode(string id, AutomationStepDescriptor step)
        : base(id) =>
        Step = step ?? throw new ArgumentNullException(nameof(step));

    public AutomationStepDescriptor Step { get; }
}

public sealed class AutomationSequenceNode : AutomationNode
{
    private readonly IReadOnlyList<AutomationNode> _children;

    public AutomationSequenceNode(string id, params AutomationNode[] children)
        : this(id, (IEnumerable<AutomationNode>)children)
    {
    }

    public AutomationSequenceNode(string id, IEnumerable<AutomationNode> children)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(children);
        _children = Array.AsReadOnly(children.ToArray());
    }

    public IReadOnlyList<AutomationNode> Children => _children;
}

public sealed class AutomationParallelNode : AutomationNode
{
    private readonly IReadOnlyList<AutomationNode> _children;

    public AutomationParallelNode(
        string id,
        int maxConcurrency,
        AutomationJoinMode joinMode,
        params AutomationNode[] children)
        : this(id, maxConcurrency, joinMode, (IEnumerable<AutomationNode>)children)
    {
    }

    public AutomationParallelNode(
        string id,
        int maxConcurrency,
        AutomationJoinMode joinMode,
        IEnumerable<AutomationNode> children)
        : base(id)
    {
        ArgumentNullException.ThrowIfNull(children);
        MaxConcurrency = maxConcurrency;
        JoinMode = joinMode;
        _children = Array.AsReadOnly(children.ToArray());
    }

    public int MaxConcurrency { get; }
    public AutomationJoinMode JoinMode { get; }
    public IReadOnlyList<AutomationNode> Children => _children;
}

public sealed class AutomationCheckpointNode : AutomationNode
{
    public AutomationCheckpointNode(string id, AutomationCheckpointKind kind)
        : base(id) =>
        Kind = kind;

    public AutomationCheckpointKind Kind { get; }
}

public sealed record AutomationWorkflowDefinition(
    string WorkflowId,
    string Version,
    AutomationNode Root);

internal abstract record CompiledAutomationNode(string Id);

internal sealed record CompiledActionNode(
    string Id,
    AutomationStepDescriptor Step) : CompiledAutomationNode(Id);

internal sealed record CompiledSequenceNode(
    string Id,
    IReadOnlyList<CompiledAutomationNode> Children) : CompiledAutomationNode(Id);

internal sealed record CompiledParallelNode(
    string Id,
    int MaxConcurrency,
    AutomationJoinMode JoinMode,
    IReadOnlyList<CompiledAutomationNode> Children) : CompiledAutomationNode(Id);

internal sealed record CompiledCheckpointNode(
    string Id,
    AutomationCheckpointKind Kind) : CompiledAutomationNode(Id);

public sealed class AutomationExecutionPlan
{
    private AutomationExecutionPlan(
        string workflowId,
        string workflowVersion,
        CompiledAutomationNode root,
        IReadOnlyList<string> nodeIds)
    {
        WorkflowId = workflowId;
        WorkflowVersion = workflowVersion;
        Root = root;
        NodeIds = nodeIds;
    }

    public string WorkflowId { get; }
    public string WorkflowVersion { get; }
    public IReadOnlyList<string> NodeIds { get; }

    internal CompiledAutomationNode Root { get; }

    public static AutomationExecutionPlan Compile(
        AutomationWorkflowDefinition definition,
        IAutomationCapabilityResolver? capabilityResolver = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.IsNullOrWhiteSpace(definition.WorkflowId))
            throw new InvalidOperationException("WorkflowId cannot be empty.");
        if (string.IsNullOrWhiteSpace(definition.Version))
            throw new InvalidOperationException("Workflow version cannot be empty.");
        ArgumentNullException.ThrowIfNull(definition.Root);

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var orderedIds = new List<string>();
        var compiled = CompileNode(definition.Root, ids, orderedIds, capabilityResolver);

        return new AutomationExecutionPlan(
            definition.WorkflowId,
            definition.Version,
            compiled,
            Array.AsReadOnly(orderedIds.ToArray()));
    }

    private static CompiledAutomationNode CompileNode(
        AutomationNode node,
        HashSet<string> ids,
        List<string> orderedIds,
        IAutomationCapabilityResolver? capabilityResolver)
    {
        if (!ids.Add(node.Id))
            throw new InvalidOperationException($"Duplicate automation node id '{node.Id}'.");

        orderedIds.Add(node.Id);

        return node switch
        {
            AutomationActionNode action => CompileAction(action, capabilityResolver),
            AutomationSequenceNode sequence => new CompiledSequenceNode(
                sequence.Id,
                CompileChildren(sequence.Children, ids, orderedIds, capabilityResolver, sequence.Id)),
            AutomationParallelNode parallel => CompileParallel(parallel, ids, orderedIds, capabilityResolver),
            AutomationCheckpointNode checkpoint => new CompiledCheckpointNode(checkpoint.Id, checkpoint.Kind),
            _ => throw new InvalidOperationException($"Unsupported automation node type '{node.GetType().FullName}'.")
        };
    }

    private static CompiledActionNode CompileAction(
        AutomationActionNode action,
        IAutomationCapabilityResolver? capabilityResolver)
    {
        action.Step.Policy.Validate();

        foreach (var capability in action.Step.RequiredCapabilities)
        {
            if (string.IsNullOrWhiteSpace(capability))
                throw new InvalidOperationException($"Step '{action.Id}' contains an empty required capability.");

            if (capabilityResolver is null)
            {
                throw new InvalidOperationException(
                    $"Step '{action.Id}' requires capability '{capability}', but no capability resolver was supplied.");
            }

            if (!capabilityResolver.IsAvailable(capability))
            {
                throw new InvalidOperationException(
                    $"Step '{action.Id}' requires unavailable capability '{capability}'.");
            }
        }

        var retry = action.Step.Policy.Retry ?? AutomationRetryPolicy.None;
        var frozenRetry = retry with
        {
            RetryableStatuses = retry.RetryableStatuses is null
                ? null
                : new HashSet<AutomationStepStatus>(retry.RetryableStatuses)
        };

        var frozenPolicy = action.Step.Policy with
        {
            Resources = action.Step.Policy.NormalizeResources(),
            Retry = frozenRetry
        };

        var frozen = new AutomationStepDescriptor(
            action.Step.Execute,
            frozenPolicy,
            action.Step.Precondition,
            action.Step.Compensation,
            action.Step.RequiredCapabilities.ToArray());

        return new CompiledActionNode(action.Id, frozen);
    }

    private static IReadOnlyList<CompiledAutomationNode> CompileChildren(
        IReadOnlyList<AutomationNode> children,
        HashSet<string> ids,
        List<string> orderedIds,
        IAutomationCapabilityResolver? capabilityResolver,
        string parentId)
    {
        if (children.Count == 0)
            throw new InvalidOperationException($"Automation node '{parentId}' must contain at least one child.");

        var result = new CompiledAutomationNode[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            if (children[i] is null)
                throw new InvalidOperationException($"Automation node '{parentId}' contains a null child.");

            result[i] = CompileNode(children[i], ids, orderedIds, capabilityResolver);
        }

        return Array.AsReadOnly(result);
    }

    private static CompiledParallelNode CompileParallel(
        AutomationParallelNode parallel,
        HashSet<string> ids,
        List<string> orderedIds,
        IAutomationCapabilityResolver? capabilityResolver)
    {
        if (parallel.MaxConcurrency <= 0)
            throw new InvalidOperationException($"Parallel node '{parallel.Id}' MaxConcurrency must be greater than zero.");

        return new CompiledParallelNode(
            parallel.Id,
            parallel.MaxConcurrency,
            parallel.JoinMode,
            CompileChildren(parallel.Children, ids, orderedIds, capabilityResolver, parallel.Id));
    }
}
