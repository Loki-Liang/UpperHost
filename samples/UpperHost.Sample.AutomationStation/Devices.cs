using UpperHost.Abstractions.Devices;

namespace UpperHost.Sample.AutomationStation;

public sealed class SafetyDoorDevice : IDevice
{
    public DeviceDescriptor Descriptor { get; } = new("door-1", "Safety Door", "UpperHost", "SIM-DOOR");
    public DeviceState State => DeviceState.Online;
    public IReadOnlyCollection<string> Capabilities { get; } = ["safety-input"];
    public bool IsClosed { get; private set; } = true;
    public void Close() => IsClosed = true;
    public void Open() => IsClosed = false;
}

public abstract record AxisCommand;
public sealed record HomeAxisCommand : AxisCommand;
public sealed record MoveAxisCommand(double Position) : AxisCommand;
public sealed record AxisResult(double Position, string Message);

public sealed class AxisDevice : IDevice, ICommandable<AxisCommand, AxisResult>
{
    public DeviceDescriptor Descriptor { get; } = new("axis-x", "X Axis", "UpperHost", "SIM-AXIS");
    public DeviceState State { get; private set; } = DeviceState.Online;
    public IReadOnlyCollection<string> Capabilities { get; } = ["command", "motion"];
    public double Position { get; private set; }
    public bool Homed { get; private set; }

    public async Task<AxisResult> ExecuteAsync(AxisCommand command, CancellationToken cancellationToken = default)
    {
        State = DeviceState.Busy;
        try
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            switch (command)
            {
                case HomeAxisCommand:
                    Position = 0;
                    Homed = true;
                    return new AxisResult(Position, "Axis homed.");
                case MoveAxisCommand move when !Homed:
                    throw new InvalidOperationException("Axis must be homed before absolute movement.");
                case MoveAxisCommand move:
                    Position = move.Position;
                    return new AxisResult(Position, $"Axis moved to {Position:0.0}.");
                default:
                    throw new NotSupportedException($"Unknown axis command {command.GetType().Name}.");
            }
        }
        finally
        {
            State = DeviceState.Online;
        }
    }
}

public sealed record CaptureCommand;
public sealed record CaptureResult(string ImageId, double Quality);

public sealed class CameraDevice : IDevice, ICommandable<CaptureCommand, CaptureResult>
{
    private int _sequence;
    public DeviceDescriptor Descriptor { get; } = new("camera-1", "Inspection Camera", "UpperHost", "SIM-CAMERA");
    public DeviceState State => DeviceState.Online;
    public IReadOnlyCollection<string> Capabilities { get; } = ["command", "capture"];

    public Task<CaptureResult> ExecuteAsync(CaptureCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = $"IMG-{Interlocked.Increment(ref _sequence):D4}";
        return Task.FromResult(new CaptureResult(id, 0.99));
    }
}
