using UpperHost.Dataflow;
using UpperHost.Sample.DataAcquisition.Acquisition;
using UpperHost.Sample.DataAcquisition.Consumers;

var device = new SimulatorAcquisitionDevice(channelCount: 4, frameCount: 500, samplePeriod: TimeSpan.FromMilliseconds(1));
await device.ConnectAsync();

var hub = new FanOutHub<SampleFrame>(new FanOutOptions(
    Capacity: 16,
    BackpressureMode: FanOutBackpressureMode.DropOldest));

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
var storagePath = Path.Combine(
    Path.GetTempPath(),
    "UpperHost",
    "DataAcquisitionSample",
    $"samples-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl");

var display = new ConsoleDisplayConsumer(
    artificialRenderDelay: TimeSpan.FromMilliseconds(8),
    renderEvery: 25);
var storage = new JsonLinesStorageConsumer(storagePath);

var displayTask = display.RunAsync(hub.Subscribe(cts.Token), cts.Token);
var storageTask = storage.RunAsync(hub.Subscribe(cts.Token), cts.Token);

try
{
    await foreach (var frame in device.ReadAsync(cts.Token))
        await hub.PublishAsync(frame, cts.Token);
}
finally
{
    await hub.DisposeAsync();
    await device.DisconnectAsync();
}

var displayStats = await displayTask;
var storedFrames = await storageTask;

Console.WriteLine();
Console.WriteLine("Acquisition complete.");
Console.WriteLine($"Produced frames: {device.ProducedFrames}");
Console.WriteLine($"Display received: {displayStats.ReceivedFrames}");
Console.WriteLine($"Display observed dropped frames: {displayStats.SequenceGaps}");
Console.WriteLine($"Storage received: {storedFrames}");
Console.WriteLine($"Storage file: {storagePath}");
