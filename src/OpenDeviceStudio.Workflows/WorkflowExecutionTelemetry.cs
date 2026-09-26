using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OpenDeviceStudio.Workflows;

internal static class WorkflowExecutionTelemetry
{
    private static readonly Meter Meter = new(
        "OpenDeviceStudio.Workflows",
        "1.0.0");

    public static readonly Counter<long> Executions =
        Meter.CreateCounter<long>("opendevicestudio.workflow.executions");
    public static readonly Counter<long> Failures =
        Meter.CreateCounter<long>("opendevicestudio.workflow.failures");
    public static readonly Counter<long> Retries =
        Meter.CreateCounter<long>("opendevicestudio.workflow.retries");
    public static readonly UpDownCounter<long> ActiveExecutions =
        Meter.CreateUpDownCounter<long>("opendevicestudio.workflow.active");
    public static readonly Histogram<double> ExecutionDurationSeconds =
        Meter.CreateHistogram<double>("opendevicestudio.workflow.duration", unit: "s");
    public static readonly Histogram<double> NodeDurationSeconds =
        Meter.CreateHistogram<double>("opendevicestudio.workflow.node.duration", unit: "s");
    public static readonly Histogram<double> ResourceWaitSeconds =
        Meter.CreateHistogram<double>("opendevicestudio.workflow.resource_wait", unit: "s");

    public static TagList ExecutionTags(
        string mode,
        WorkflowExecutionStatus status)
    {
        TagList tags = [];
        tags.Add("opendevicestudio.workflow.mode", mode);
        tags.Add("opendevicestudio.workflow.outcome", status.ToString());
        return tags;
    }

    public static TagList NodeTags(
        WorkflowNode node,
        WorkflowNodeStatus status)
    {
        TagList tags = [];
        tags.Add("opendevicestudio.workflow.node_type", NodeType(node));
        tags.Add("opendevicestudio.workflow.outcome", status.ToString());
        return tags;
    }

    public static TagList ResourceTags(WorkflowActionNode node)
    {
        TagList tags = [];
        tags.Add("opendevicestudio.workflow.node_type", "action");
        tags.Add(
            "opendevicestudio.workflow.resource_class",
            node.Policy.Resources is { Count: > 0 } ? "logical" : "none");
        return tags;
    }

    private static string NodeType(WorkflowNode node) =>
        node switch
        {
            WorkflowActionNode => "action",
            WorkflowSequenceNode => "sequence",
            WorkflowParallelNode => "parallel",
            WorkflowSafeCheckpointNode => "checkpoint",
            _ => "unknown"
        };
}
