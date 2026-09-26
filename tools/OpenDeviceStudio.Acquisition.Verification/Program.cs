using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenDeviceStudio.Abstractions.Storage;
using OpenDeviceStudio.Dataflow;
using OpenDeviceStudio.Storage.FileSystem;

namespace OpenDeviceStudio.Acquisition.Verification;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var options = CliOptions.Parse(args);
        Directory.CreateDirectory(options.ArtifactDirectory);
        var summaryPath = Path.Combine(options.ArtifactDirectory, "summary.json");
        VerificationSummary? summary = null;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var profileBytes = await File.ReadAllBytesAsync(options.ProfilePath);
            var profile = JsonSerializer.Deserialize<VerificationProfile>(
                profileBytes,
                JsonOptions.Strict)
                ?? throw new InvalidDataException("Verification profile is empty.");

            ValidateProfile(profile);
            var profileHash = Convert.ToHexString(SHA256.HashData(profileBytes)).ToLowerInvariant();
            summary = await RunAsync(profile, profileHash, options, stopwatch);
            await WriteSummaryAsync(summaryPath, summary);
            Console.WriteLine($"PASS {profile.Id}; summary={summaryPath}");
            return 0;
        }
        catch (Exception ex)
        {
            summary ??= VerificationSummary.Failed(
                exactSha: ResolveExactSha(),
                profilePath: options.ProfilePath,
                seed: options.Seed,
                elapsed: stopwatch.Elapsed,
                error: ex);
            await WriteSummaryAsync(summaryPath, summary);
            Console.Error.WriteLine($"FAIL {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<VerificationSummary> RunAsync(
        VerificationProfile profile,
        string profileHash,
        CliOptions options,
        Stopwatch stopwatch)
    {
        using var watchdog = new CancellationTokenSource(options.Timeout);
        var token = watchdog.Token;
        var rawRoot = Path.Combine(options.ArtifactDirectory, "raw");
        Directory.CreateDirectory(rawRoot);

        var blocksPerSource = checked((int)Math.Ceiling(
            profile.DurationSeconds * (double)profile.SampleRateHz / profile.BlockSizeSamples));
        if (options.MaxBlocksPerSource is { } cap)
            blocksPerSource = Math.Min(blocksPerSource, cap);

        var expectedBlocks = checked(blocksPerSource * profile.SourceCount);
        var configHash = profileHash[..16];
        var rawOptions = new FileSystemRawRecorderOptions(
            RootDirectory: rawRoot,
            QueueCapacity: profile.RawRecorder.QueueCapacityBlocks,
            MaxSegmentBytes: 256L * 1024 * 1024,
            MaxSegmentDuration: TimeSpan.FromMinutes(10),
            HardMinimumFreeBytes: 0,
            WarningFreeBytes: 0,
            MaxSessionBytes: Math.Max(
                512L * 1024 * 1024,
                (long)expectedBlocks * profile.BlockSizeSamples * profile.ChannelsPerSource * profile.BytesPerSample * 2),
            Durability: ParseDurability(profile.RawRecorder.Durability),
            FreeSpaceCheckIntervalBlocks: 64);

        await using var recorder = new FileSystemRawRecorder(rawOptions);
        var sourceIdentities = Enumerable.Range(0, profile.SourceCount)
            .Select(index => new RawRecordingSourceIdentity(SourceId(index), 1))
            .ToArray();
        var sessionId = $"verify-{profile.Id}-{options.Seed}-{Guid.NewGuid():N}";
        await recorder.PrepareAsync(
            new RawRecordingSessionDescriptor(
                sessionId,
                sourceIdentities,
                configHash,
                DateTimeOffset.UtcNow),
            cancellationToken: token);

        await using var router = new StreamRouter<CanonicalRawBlock>();
        var subscriptions = new Dictionary<string, StreamRouter<CanonicalRawBlock>.StreamBranchSubscription>(
            StringComparer.Ordinal);
        long requiredDelivered = 0;
        long optionalDelivered = 0;

        foreach (var branch in profile.RouterBranches)
        {
            var branchOptions = new StreamBranchOptions(
                branch.Id,
                branch.Id,
                branch.CapacityBlocks,
                ParseDelivery(branch.Delivery),
                ParseOverflow(branch.Overflow),
                branch.Delivery == "Required"
                    ? StreamBranchFailurePolicy.Propagate
                    : StreamBranchFailurePolicy.Isolate);

            if (branch.Id == "processing")
            {
                subscriptions[branch.Id] = router.RegisterBranch(
                    branchOptions,
                    async (item, cancellationToken) =>
                    {
                        await ApplyDelayAsync(profile, "Processing", item.PublishSequence, cancellationToken);
                        Interlocked.Increment(ref requiredDelivered);
                    });
            }
            else if (branch.Id == "presentation")
            {
                subscriptions[branch.Id] = router.RegisterBranch(
                    branchOptions,
                    async (item, cancellationToken) =>
                    {
                        await ApplyDelayAsync(profile, "Presentation", item.PublishSequence, cancellationToken);
                        if (ShouldThrow(profile, item.PublishSequence))
                            throw new InvalidOperationException(
                                $"Injected optional presentation fault at publish sequence {item.PublishSequence}.");
                        Interlocked.Increment(ref optionalDelivered);
                    });
            }
            else
            {
                throw new InvalidOperationException(
                    $"Verification runner has no adapter for Router branch '{branch.Id}'.");
            }
        }

        router.Start();
        var payloadBytes = checked(profile.BlockSizeSamples * profile.ChannelsPerSource * profile.BytesPerSample);
        var blockDuration = TimeSpan.FromSeconds(
            profile.BlockSizeSamples / (double)profile.SampleRateHz);
        var startedAt = Stopwatch.GetTimestamp();

        for (var blockIndex = 0; blockIndex < blocksPerSource; blockIndex++)
        {
            for (var sourceIndex = 0; sourceIndex < profile.SourceCount; sourceIndex++)
            {
                token.ThrowIfCancellationRequested();
                var sequenceStart = checked((long)blockIndex * profile.BlockSizeSamples);
                var block = CreateBlock(
                    profile,
                    sourceIndex,
                    sequenceStart,
                    payloadBytes,
                    options.Seed,
                    configHash);

                var rawAcceptance = await recorder.AcceptAsync(block, token);
                if (!rawAcceptance.Accepted)
                {
                    throw new InvalidOperationException(
                        $"Raw acceptance failed: {rawAcceptance.Status}: {rawAcceptance.Reason}");
                }

                var routed = await router.PublishAsync(block, token);
                if (routed.HasRequiredFailure)
                {
                    throw new InvalidOperationException(
                        $"Required Router delivery failed at publish {routed.PublishSequence}: {routed.RouterFault}");
                }
            }

            if (profile.Mode == "WallClockPaced")
            {
                var target = TimeSpan.FromTicks(blockDuration.Ticks * (blockIndex + 1L));
                var elapsed = Stopwatch.GetElapsedTime(startedAt);
                var delay = target - elapsed;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, token);
            }
        }

        await recorder.StopAsync(token);
        await recorder.FinalizeAsync(token);
        var routerTerminal = await router.CompleteAsync(StreamCompletionMode.Drain, token);

        var raw = recorder.Snapshot;
        Require(raw.State == RawRecorderState.Completed, $"Raw terminal state is {raw.State}.");
        Require(raw.AcceptedBlocks == expectedBlocks, $"Raw accepted {raw.AcceptedBlocks}, expected {expectedBlocks}.");
        Require(raw.WrittenBlocks == expectedBlocks, $"Raw wrote {raw.WrittenBlocks}, expected {expectedBlocks}.");
        Require(raw.AcceptedBytes == raw.WrittenBytes, "Raw accepted/written byte counts differ.");
        Require(raw.SequenceGapCount == 0, $"Raw sequence gaps={raw.SequenceGapCount}.");
        Require(raw.DuplicateBlockCount == 0, $"Raw duplicates={raw.DuplicateBlockCount}.");
        Require(raw.OutOfOrderBlockCount == 0, $"Raw out-of-order={raw.OutOfOrderBlockCount}.");
        Require(raw.QueueDepth == 0, $"Raw queue depth did not quiesce: {raw.QueueDepth}.");
        Require(raw.QueueHighWater <= raw.QueueCapacity, "Raw queue high-water exceeded capacity.");
        Require(routerTerminal.State == StreamRouterState.Completed, $"Router terminal state is {routerTerminal.State}.");

        var processing = subscriptions["processing"].GetSnapshot();
        Require(processing.Delivered == expectedBlocks, $"Processing delivered {processing.Delivered}, expected {expectedBlocks}.");
        Require(processing.Dropped == 0, "Required processing dropped data.");
        Require(processing.Rejected == 0, "Required processing rejected data.");
        Require(processing.QueueDepth == 0, "Required processing queue did not quiesce.");
        Require(processing.HighWatermark <= processing.Capacity, "Required processing high-water exceeded capacity.");

        var presentation = subscriptions["presentation"].GetSnapshot();
        Require(presentation.QueueDepth == 0, "Presentation queue did not quiesce.");
        Require(presentation.HighWatermark <= presentation.Capacity, "Presentation high-water exceeded capacity.");

        var stored = await FileSystemRawArtifactReader.ReadAllAsync(raw.SessionDirectory!, token);
        Require(stored.Count == expectedBlocks, $"Raw artifact contains {stored.Count} blocks, expected {expectedBlocks}.");
        foreach (var sourceGroup in stored.GroupBy(static block => block.SourceId))
        {
            long expectedSequence = 0;
            foreach (var block in sourceGroup.OrderBy(static block => block.SequenceStart))
            {
                Require(block.SequenceStart == expectedSequence,
                    $"Source {sourceGroup.Key} sequence {block.SequenceStart}, expected {expectedSequence}.");
                expectedSequence += profile.BlockSizeSamples;
            }
        }

        var recovery = await FileSystemRawRecoveryScanner.ScanAsync(raw.SessionDirectory!, token);
        Require(recovery.IsComplete, $"Raw recovery is incomplete: {string.Join("; ", recovery.Issues.Select(static item => item.Message))}");
        Require(recovery.VerifiedBlocks == expectedBlocks, "Recovery verified block count differs.");

        stopwatch.Stop();
        return new VerificationSummary(
            Status: "Passed",
            ExactSha: ResolveExactSha(),
            ProfileId: profile.Id,
            ProfilePath: options.ProfilePath,
            ProfileSha256: profileHash,
            EvidenceKind: profile.EvidenceKind,
            SourceMode: profile.Mode,
            Seed: options.Seed,
            StartedAtUtc: DateTimeOffset.UtcNow - stopwatch.Elapsed,
            ElapsedMilliseconds: stopwatch.Elapsed.TotalMilliseconds,
            Environment: EnvironmentFingerprint.Capture(),
            RequestedDurationSeconds: profile.DurationSeconds,
            BlocksPerSource: blocksPerSource,
            ExpectedBlocks: expectedBlocks,
            Raw: raw,
            Router: routerTerminal,
            Branches: subscriptions.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.GetSnapshot(),
                StringComparer.Ordinal),
            RequiredDelivered: requiredDelivered,
            OptionalDelivered: optionalDelivered,
            Recovery: recovery,
            AppliedFaults: DescribeFaultDisposition(profile),
            FirstFailingInvariant: null,
            ErrorType: null,
            Error: null);
    }

    private static CanonicalRawBlock CreateBlock(
        VerificationProfile profile,
        int sourceIndex,
        long sequenceStart,
        int payloadBytes,
        int seed,
        string configHash)
    {
        var payload = new byte[payloadBytes];
        var state = unchecked((uint)(seed ^ sourceIndex * 7919 ^ (int)sequenceStart * 397));
        for (var index = 0; index < payload.Length; index++)
        {
            state = unchecked(state * 1664525u + 1013904223u);
            payload[index] = (byte)(state >> 24);
        }

        return CanonicalRawBlock.CopyFrom(
            sourceId: SourceId(sourceIndex),
            deviceId: $"synthetic-{sourceIndex}",
            connectionEpoch: 1,
            sequenceStart: sequenceStart,
            sequenceCount: profile.BlockSizeSamples,
            sampleCount: profile.BlockSizeSamples,
            channelCount: profile.ChannelsPerSource,
            channelLayoutId: $"channels-{profile.ChannelsPerSource}",
            numericRepresentation: ParseNumeric(profile.NumericType),
            byteOrder: RawByteOrder.LittleEndian,
            sampleRateHz: profile.SampleRateHz,
            deviceTimestamp: sequenceStart,
            hostMonotonicTimestamp: sequenceStart,
            wallClockTimestamp: DateTimeOffset.UnixEpoch.AddTicks(
                checked((long)(sequenceStart * TimeSpan.TicksPerSecond / (double)profile.SampleRateHz))),
            qualityFlags: 0,
            protocolVersion: "synthetic-v1",
            canonicalizationVersion: "verification-v1",
            configurationHash: configHash,
            payload: payload);
    }

    private static async ValueTask ApplyDelayAsync(
        VerificationProfile profile,
        string target,
        long publishSequence,
        CancellationToken token)
    {
        var fault = profile.FaultSchedule.FirstOrDefault(item =>
            item.Target == target &&
            item.Kind == "Delay" &&
            item.AtSequence == publishSequence &&
            item.DurationMs is > 0);
        if (fault?.DurationMs is { } duration)
            await Task.Delay(duration, token);
    }

    private static bool ShouldThrow(VerificationProfile profile, long publishSequence) =>
        profile.FaultSchedule.Any(item =>
            item.Target is "Presentation" or "RouterOptional" &&
            item.Kind == "Throw" &&
            item.AtSequence == publishSequence);

    private static IReadOnlyList<FaultDisposition> DescribeFaultDisposition(VerificationProfile profile) =>
        profile.FaultSchedule.Select(fault =>
        {
            var applied = fault.Target switch
            {
                "Processing" when fault.Kind == "Delay" => true,
                "Presentation" when fault.Kind is "Delay" or "Throw" => true,
                "RouterOptional" when fault.Kind == "Throw" => true,
                _ => false,
            };
            return new FaultDisposition(
                fault.Target,
                fault.Kind,
                fault.AtSequence,
                applied ? "AppliedBySoakRunner" : "CoveredByDedicatedFaultTestOrBlockedAdapter");
        }).ToArray();

    private static RawDurabilityLevel ParseDurability(string value) =>
        Enum.Parse<RawDurabilityLevel>(value, ignoreCase: false);

    private static RawNumericRepresentation ParseNumeric(string value) => value switch
    {
        "int16" => RawNumericRepresentation.Int16,
        "int32" => RawNumericRepresentation.Int32,
        "float32" => RawNumericRepresentation.Float32,
        "float64" => RawNumericRepresentation.Float64,
        _ => throw new InvalidDataException($"Unsupported numericType '{value}'."),
    };

    private static StreamBranchDelivery ParseDelivery(string value) =>
        Enum.Parse<StreamBranchDelivery>(value, ignoreCase: false);

    private static StreamOverflowPolicy ParseOverflow(string value) =>
        Enum.Parse<StreamOverflowPolicy>(value, ignoreCase: false);

    private static void ValidateProfile(VerificationProfile profile)
    {
        Require(profile.SchemaVersion == 1, "Profile schemaVersion must be 1.");
        Require(profile.EvidenceKind is "Synthetic" or "Loopback" or "Hardware", "Invalid evidenceKind.");
        Require(profile.Mode is "VirtualTimeDeterministic" or "WallClockPaced" or "MaxThroughput", "Invalid source mode.");
        Require(profile.SourceCount > 0, "sourceCount must be positive.");
        Require(profile.ChannelsPerSource > 0, "channelsPerSource must be positive.");
        Require(profile.BytesPerSample > 0, "bytesPerSample must be positive.");
        Require(profile.SampleRateHz > 0, "sampleRateHz must be positive.");
        Require(profile.BlockSizeSamples > 0, "blockSizeSamples must be positive.");
        Require(profile.RawRecorder.QueueCapacityBlocks > 0, "Raw queue capacity must be positive.");
        Require(profile.RouterBranches.Count > 0, "At least one Router branch is required.");
        Require(profile.RouterBranches.Any(static item => item.Id == "processing" && item.Delivery == "Required"),
            "Verification runner requires a Required processing branch.");
        Require(profile.RouterBranches.Any(static item => item.Id == "presentation" && item.Delivery == "Optional"),
            "Verification runner requires an Optional presentation branch.");
        var expectedRate = checked(
            (long)profile.SourceCount * profile.ChannelsPerSource * profile.BytesPerSample * profile.SampleRateHz);
        Require(profile.ExpectedRawBytesPerSecond == expectedRate,
            $"expectedRawBytesPerSecond={profile.ExpectedRawBytesPerSecond}, calculated={expectedRate}.");
    }

    private static string SourceId(int index) => $"synthetic-{index:D2}";

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static string ResolveExactSha()
    {
        var sha = Environment.GetEnvironmentVariable("GITHUB_SHA")
            ?? Environment.GetEnvironmentVariable("ACQ_EXACT_SHA");
        if (!string.IsNullOrWhiteSpace(sha))
            return sha;

        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is not null)
            {
                process.WaitForExit(3000);
                var output = process.StandardOutput.ReadToEnd().Trim();
                if (process.ExitCode == 0 && output.Length >= 7)
                    return output;
            }
        }
        catch
        {
        }

        return "unknown";
    }

    private static Task WriteSummaryAsync(string path, VerificationSummary summary) =>
        File.WriteAllTextAsync(
            path,
            JsonSerializer.Serialize(summary, JsonOptions.Output),
            Encoding.UTF8);
}

internal sealed record CliOptions(
    string ProfilePath,
    string ArtifactDirectory,
    int Seed,
    int? MaxBlocksPerSource,
    TimeSpan Timeout)
{
    public static CliOptions Parse(string[] args)
    {
        string? profile = null;
        string? artifact = null;
        var seed = 6201;
        int? maxBlocks = null;
        var timeoutSeconds = 900;

        for (var index = 0; index < args.Length; index++)
        {
            string Next(string name)
            {
                if (++index >= args.Length)
                    throw new ArgumentException($"Missing value for {name}.");
                return args[index];
            }

            switch (args[index])
            {
                case "--profile":
                    profile = Next("--profile");
                    break;
                case "--artifact-dir":
                    artifact = Next("--artifact-dir");
                    break;
                case "--seed":
                    seed = int.Parse(Next("--seed"), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--max-blocks-per-source":
                    maxBlocks = int.Parse(Next("--max-blocks-per-source"), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--timeout-seconds":
                    timeoutSeconds = int.Parse(Next("--timeout-seconds"), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(profile))
            throw new ArgumentException("--profile is required.");
        if (string.IsNullOrWhiteSpace(artifact))
            throw new ArgumentException("--artifact-dir is required.");
        if (maxBlocks is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBlocks));
        if (timeoutSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));

        return new CliOptions(
            Path.GetFullPath(profile),
            Path.GetFullPath(artifact),
            seed,
            maxBlocks,
            TimeSpan.FromSeconds(timeoutSeconds));
    }
}

internal static class JsonOptions
{
    public static JsonSerializerOptions Strict { get; } = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static JsonSerializerOptions Output { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}

internal sealed record VerificationProfile(
    int SchemaVersion,
    string Id,
    string EvidenceKind,
    string Mode,
    int SourceCount,
    int ChannelsPerSource,
    string NumericType,
    int BytesPerSample,
    int SampleRateHz,
    int BlockSizeSamples,
    int DurationSeconds,
    long ExpectedRawBytesPerSecond,
    RawRecorderProfile RawRecorder,
    IReadOnlyList<RouterBranchProfile> RouterBranches,
    IReadOnlyList<string> PipelineStages,
    PresentationProfile Presentation,
    IReadOnlyList<FaultProfile> FaultSchedule);

internal sealed record RawRecorderProfile(int QueueCapacityBlocks, string Durability);
internal sealed record RouterBranchProfile(string Id, string Delivery, int CapacityBlocks, string Overflow);
internal sealed record PresentationProfile(int ViewportSamples, int TargetFps);
internal sealed record FaultProfile(string Target, string Kind, long AtSequence, int? DurationMs);
internal sealed record FaultDisposition(string Target, string Kind, long AtSequence, string Disposition);

internal sealed record EnvironmentFingerprint(
    string OsDescription,
    string FrameworkDescription,
    string ProcessArchitecture,
    string OsArchitecture,
    int ProcessorCount,
    string MachineName,
    string? RunnerName)
{
    public static EnvironmentFingerprint Capture() =>
        new(
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount,
            Environment.MachineName,
            Environment.GetEnvironmentVariable("RUNNER_NAME"));
}

internal sealed record VerificationSummary(
    string Status,
    string ExactSha,
    string ProfileId,
    string ProfilePath,
    string? ProfileSha256,
    string? EvidenceKind,
    string? SourceMode,
    int Seed,
    DateTimeOffset StartedAtUtc,
    double ElapsedMilliseconds,
    EnvironmentFingerprint Environment,
    int? RequestedDurationSeconds,
    int? BlocksPerSource,
    int? ExpectedBlocks,
    RawRecorderSnapshot? Raw,
    StreamRouterSnapshot? Router,
    IReadOnlyDictionary<string, StreamBranchSnapshot>? Branches,
    long? RequiredDelivered,
    long? OptionalDelivered,
    RawRecoveryReport? Recovery,
    IReadOnlyList<FaultDisposition>? AppliedFaults,
    string? FirstFailingInvariant,
    string? ErrorType,
    string? Error)
{
    public static VerificationSummary Failed(
        string exactSha,
        string profilePath,
        int seed,
        TimeSpan elapsed,
        Exception error) =>
        new(
            "Failed",
            exactSha,
            Path.GetFileNameWithoutExtension(profilePath),
            profilePath,
            null,
            null,
            null,
            seed,
            DateTimeOffset.UtcNow - elapsed,
            elapsed.TotalMilliseconds,
            EnvironmentFingerprint.Capture(),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            error.Message,
            error.GetType().FullName,
            error.ToString());
}
