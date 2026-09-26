using OpenDeviceStudio.Acquisition;
using OpenDeviceStudio.Sample.DataAcquisition.Acquisition;
using OpenDeviceStudio.Sample.DataAcquisition.Consumers;

var device = new SimulatorAcquisitionDevice(
    channelCount: 4,
    frameCount: 500,
    samplePeriod: TimeSpan.FromMilliseconds(1));

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var storagePath = Path.Combine(
    Path.GetTempPath(),
    "OpenDeviceStudio",
    "DataAcquisitionSample",
    $"samples-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl");

var dataflow = new SampleDataflowRuntime(storagePath);
var source = new SessionManagedSimulatorSource(device, dataflow.PublishAsync);

await using var manager = new AcquisitionSessionManager();
await using var session = manager.CreateSession(new AcquisitionSessionDefinition(
    AcquisitionSessionMode.LiveAcquisition,
    [source],
    requiredComponents:
    [
        new AcquisitionRequiredComponentRegistration(dataflow)
    ]));

await session.StartAsync(cts.Token);
await source.ProductionCompleted.WaitAsync(cts.Token);
var result = await session.StopAsync(cts.Token);

Console.WriteLine();
Console.WriteLine($"Acquisition session: {result.SessionId}");
Console.WriteLine($"Session state: {result.TerminalState}");
Console.WriteLine($"Produced frames: {device.ProducedFrames}");
Console.WriteLine($"Display received: {dataflow.DisplayStatistics?.ReceivedFrames ?? 0}");
Console.WriteLine($"Display observed dropped frames: {dataflow.DisplayStatistics?.SequenceGaps ?? 0}");
Console.WriteLine($"Storage received: {dataflow.StoredFrames}");
Console.WriteLine($"Storage file: {dataflow.StoragePath}");
