using System.Collections.ObjectModel;

namespace OpenDeviceStudio.Workflows;

public sealed class WorkflowExecutionPlan
{
    internal WorkflowExecutionPlan(
        string workflowId,
        string version,
        WorkflowNode root,
        IReadOnlyDictionary<string, WorkflowNode> nodes)
    {
        WorkflowId = workflowId;
        Version = version;
        Root = root;
        Nodes = nodes;
    }

    public string WorkflowId { get; }
    public string Version { get; }
    public WorkflowNode Root { get; }
    public IReadOnlyDictionary<string, WorkflowNode> Nodes { get; }
}

public static class WorkflowPlanCompiler
{
    public static WorkflowExecutionPlan Compile(WorkflowExecutionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.ValidateIdentity();

        var nodes = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        ValidateNode(definition.Root, nodes, insideParallel: false);

        return new WorkflowExecutionPlan(
            definition.WorkflowId.Trim(),
            definition.Version.Trim(),
            definition.Root,
            new ReadOnlyDictionary<string, WorkflowNode>(nodes));
    }

    private static void ValidateNode(
        WorkflowNode node,
        IDictionary<string, WorkflowNode> nodes,
        bool insideParallel)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!nodes.TryAdd(node.NodeId, node))
            throw new InvalidOperationException($"Duplicate workflow node id '{node.NodeId}'.");

        switch (node)
        {
            case WorkflowActionNode action:
                action.Policy.Validate();
                break;

            case WorkflowSequenceNode sequence:
                if (sequence.Children.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Sequence node '{sequence.NodeId}' must contain at least one child.");
                }

                foreach (var child in sequence.Children)
                    ValidateNode(child, nodes, insideParallel);
                break;

            case WorkflowParallelNode parallel:
                if (parallel.Children.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Parallel node '{parallel.NodeId}' must contain at least one child.");
                }

                if (parallel.MaxConcurrency > parallel.Children.Count)
                {
                    throw new InvalidOperationException(
                        $"Parallel node '{parallel.NodeId}' max concurrency cannot exceed its child count.");
                }

                foreach (var child in parallel.Children)
                    ValidateNode(child, nodes, insideParallel: true);
                break;

            case WorkflowSafeCheckpointNode checkpoint when insideParallel:
                throw new InvalidOperationException(
                    $"Safe checkpoint '{checkpoint.NodeId}' cannot be placed inside a Parallel node because pause must have one deterministic execution boundary.");

            case WorkflowSafeCheckpointNode:
                break;

            default:
                throw new NotSupportedException(
                    $"Workflow node type '{node.GetType().FullName}' is not supported.");
        }
    }
}
