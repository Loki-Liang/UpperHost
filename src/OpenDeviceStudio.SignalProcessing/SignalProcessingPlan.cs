using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using OpenDeviceStudio.Dataflow;

namespace OpenDeviceStudio.SignalProcessing;

public sealed record SignalCompiledStageDescriptor(
    string StageId,
    string InputStageId,
    string Version,
    string ConfigurationHash,
    bool Stateful,
    bool RequiresContinuity,
    bool RequiresOrderedInput,
    SignalGapPolicy GapPolicy,
    TimeSpan AlgorithmicDelay,
    SignalWindowContract? Window,
    SignalEdgeOptions Edge,
    SignalDescriptor InputDescriptor,
    SignalDescriptor OutputDescriptor);

internal sealed record CompiledSignalStage<T>(
    SignalStageRegistration<T> Registration,
    SignalDescriptor InputDescriptor,
    SignalDescriptor OutputDescriptor)
    where T : unmanaged
{
    public string StageId => Registration.Factory.StageId;
    public string InputStageId => Registration.InputStageId;
}

public sealed class CompiledSignalProcessingPlan<T>
    where T : unmanaged
{
    private readonly IReadOnlyList<CompiledSignalStage<T>> _compiledStages;

    internal CompiledSignalProcessingPlan(
        string graphVersion,
        string planHash,
        SignalDescriptor inputDescriptor,
        IReadOnlyList<CompiledSignalStage<T>> compiledStages)
    {
        GraphVersion = graphVersion;
        PlanHash = planHash;
        InputDescriptor = inputDescriptor;
        _compiledStages = compiledStages.ToArray();
        TopologicalOrder = _compiledStages
            .Select(static stage => stage.StageId)
            .ToArray();
        StageDescriptors = new ReadOnlyDictionary<string, SignalCompiledStageDescriptor>(
            _compiledStages.ToDictionary(
                static stage => stage.StageId,
                static stage =>
                {
                    var factory = stage.Registration.Factory;
                    return new SignalCompiledStageDescriptor(
                        factory.StageId,
                        stage.Registration.InputStageId,
                        factory.Version,
                        factory.ConfigurationHash,
                        factory.IsStateful,
                        factory.RequiresContinuity,
                        factory.RequiresOrderedInput,
                        factory.GapPolicy,
                        factory.AlgorithmicDelay,
                        factory.Window,
                        stage.Registration.Edge,
                        stage.InputDescriptor,
                        stage.OutputDescriptor);
                },
                StringComparer.Ordinal));
    }

    public string GraphVersion { get; }
    public string PlanHash { get; }
    public SignalDescriptor InputDescriptor { get; }
    public IReadOnlyList<string> TopologicalOrder { get; }
    public IReadOnlyDictionary<string, SignalCompiledStageDescriptor> StageDescriptors { get; }

    internal IReadOnlyList<CompiledSignalStage<T>> CompiledStages => _compiledStages;

    public SignalProcessingRuntime<T> CreateRuntime(
        string sessionId,
        string processingEpoch,
        TimeProvider? timeProvider = null) =>
        new(this, sessionId, processingEpoch, timeProvider);
}

public static class SignalProcessingCompiler
{
    public static CompiledSignalProcessingPlan<T> Compile<T>(
        SignalProcessingDefinition<T> definition)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.InputDescriptor.Validate();

        if (definition.Stages.Count == 0)
            throw new ArgumentException("Signal processing graph requires at least one stage.", nameof(definition));

        var byId = new Dictionary<string, SignalStageRegistration<T>>(StringComparer.Ordinal);
        foreach (var registration in definition.Stages)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ArgumentNullException.ThrowIfNull(registration.Factory);
            ArgumentNullException.ThrowIfNull(registration.Edge);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.InputStageId);

            var factory = registration.Factory;
            ArgumentException.ThrowIfNullOrWhiteSpace(factory.StageId);
            ArgumentException.ThrowIfNullOrWhiteSpace(factory.Version);
            ArgumentException.ThrowIfNullOrWhiteSpace(factory.ConfigurationHash);

            if (string.Equals(factory.StageId, SignalProcessingStageIds.RawInput, StringComparison.Ordinal))
                throw new ArgumentException($"StageId '{SignalProcessingStageIds.RawInput}' is reserved.");

            if (!byId.TryAdd(factory.StageId, registration))
                throw new ArgumentException($"Duplicate Signal StageId '{factory.StageId}'.");

            ValidateEdge(factory, registration.Edge);
        }

        foreach (var registration in definition.Stages)
        {
            if (!string.Equals(
                    registration.InputStageId,
                    SignalProcessingStageIds.RawInput,
                    StringComparison.Ordinal) &&
                !byId.ContainsKey(registration.InputStageId))
            {
                throw new ArgumentException(
                    $"Stage '{registration.Factory.StageId}' references unresolved InputStage '{registration.InputStageId}'.");
            }
        }

        var orderedIds = TopologicalSort(byId);
        var descriptors = new Dictionary<string, SignalDescriptor>(StringComparer.Ordinal)
        {
            [SignalProcessingStageIds.RawInput] = definition.InputDescriptor
        };
        var compiled = new List<CompiledSignalStage<T>>(orderedIds.Count);

        foreach (var stageId in orderedIds)
        {
            var registration = byId[stageId];
            var input = descriptors[registration.InputStageId];

            try
            {
                registration.Factory.ValidateInput(input);
            }
            catch (Exception error)
            {
                throw new ArgumentException(
                    $"Stage '{stageId}' rejected descriptor from '{registration.InputStageId}': {error.Message}",
                    nameof(definition),
                    error);
            }

            SignalDescriptor output;
            try
            {
                output = registration.Factory.DescribeOutput(input)
                    ?? throw new InvalidOperationException("Stage returned a null output descriptor.");
                output.Validate();
            }
            catch (Exception error)
            {
                throw new ArgumentException(
                    $"Stage '{stageId}' produced an invalid output descriptor: {error.Message}",
                    nameof(definition),
                    error);
            }

            descriptors[stageId] = output;
            compiled.Add(new CompiledSignalStage<T>(registration, input, output));
        }

        var planHash = ComputePlanHash(definition, compiled);
        return new CompiledSignalProcessingPlan<T>(
            definition.GraphVersion,
            planHash,
            definition.InputDescriptor,
            compiled);
    }

    private static void ValidateEdge<T>(
        ISignalStageFactory<T> factory,
        SignalEdgeOptions edge)
        where T : unmanaged
    {
        var lossy = edge.Overflow is
            StreamOverflowPolicy.DropOldest or
            StreamOverflowPolicy.DropNewest or
            StreamOverflowPolicy.Latest;

        if (edge.Delivery == StreamBranchDelivery.Required && lossy)
        {
            throw new ArgumentException(
                $"Required stage '{factory.StageId}' cannot use lossy overflow '{edge.Overflow}'.");
        }

        if (edge.Delivery == StreamBranchDelivery.Required &&
            edge.FailurePolicy == StreamBranchFailurePolicy.Isolate)
        {
            throw new ArgumentException(
                $"Required stage '{factory.StageId}' must propagate failures.");
        }

        if (edge.Delivery == StreamBranchDelivery.Optional &&
            edge.Overflow == StreamOverflowPolicy.Wait)
        {
            throw new ArgumentException(
                $"Optional stage '{factory.StageId}' cannot use Wait because it would backpressure the required pipeline.");
        }

        if (edge.Overflow == StreamOverflowPolicy.Latest && edge.Capacity != 1)
        {
            throw new ArgumentException(
                $"Stage '{factory.StageId}' uses Latest overflow and therefore requires Capacity=1.");
        }

        if (factory.RequiresContinuity && lossy)
        {
            throw new ArgumentException(
                $"Continuity-required stage '{factory.StageId}' cannot be attached through a lossy edge.");
        }
    }

    private static IReadOnlyList<string> TopologicalSort<T>(
        IReadOnlyDictionary<string, SignalStageRegistration<T>> byId)
        where T : unmanaged
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordered = new List<string>(byId.Count);
        var stack = new List<string>();

        foreach (var stageId in byId.Keys.OrderBy(static item => item, StringComparer.Ordinal))
            Visit(stageId);

        return ordered;

        void Visit(string stageId)
        {
            if (state.TryGetValue(stageId, out var existing))
            {
                if (existing == 2)
                    return;

                if (existing == 1)
                {
                    var cycleStart = stack.IndexOf(stageId);
                    var cycle = cycleStart >= 0
                        ? stack.Skip(cycleStart).Concat([stageId])
                        : stack.Concat([stageId]);
                    throw new ArgumentException(
                        "Signal processing graph contains a cycle: " +
                        string.Join(" -> ", cycle));
                }
            }

            state[stageId] = 1;
            stack.Add(stageId);

            var input = byId[stageId].InputStageId;
            if (!string.Equals(input, SignalProcessingStageIds.RawInput, StringComparison.Ordinal))
                Visit(input);

            stack.RemoveAt(stack.Count - 1);
            state[stageId] = 2;
            ordered.Add(stageId);
        }
    }

    private static string ComputePlanHash<T>(
        SignalProcessingDefinition<T> definition,
        IReadOnlyList<CompiledSignalStage<T>> stages)
        where T : unmanaged
    {
        var builder = new StringBuilder();
        builder.Append("graph=").Append(definition.GraphVersion).Append('\n');
        builder.Append("input=").Append(definition.InputDescriptor.Canonical()).Append('\n');

        foreach (var stage in stages)
        {
            var factory = stage.Registration.Factory;
            var edge = stage.Registration.Edge;
            builder
                .Append(factory.StageId).Append('|')
                .Append(stage.Registration.InputStageId).Append('|')
                .Append(factory.Version).Append('|')
                .Append(factory.ConfigurationHash).Append('|')
                .Append(factory.IsStateful).Append('|')
                .Append(factory.RequiresContinuity).Append('|')
                .Append(factory.RequiresOrderedInput).Append('|')
                .Append(factory.GapPolicy).Append('|')
                .Append(factory.AlgorithmicDelay.Ticks).Append('|')
                .Append(edge.Capacity).Append('|')
                .Append(edge.Delivery).Append('|')
                .Append(edge.Overflow).Append('|')
                .Append(edge.FailurePolicy).Append('|')
                .Append(stage.InputDescriptor.Canonical()).Append('|')
                .Append(stage.OutputDescriptor.Canonical())
                .Append('\n');
        }

        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
