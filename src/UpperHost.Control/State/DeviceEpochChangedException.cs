namespace UpperHost.Control.State;

public sealed class DeviceEpochChangedException : InvalidOperationException
{
    public DeviceEpochChangedException(
        string deviceId,
        long startedEpoch,
        long currentEpoch)
        : base(
            $"Device '{deviceId}' changed connection epoch from {startedEpoch} to {currentEpoch} " +
            "before the observation could be committed.")
    {
        DeviceId = deviceId;
        StartedEpoch = startedEpoch;
        CurrentEpoch = currentEpoch;
    }

    public string DeviceId { get; }
    public long StartedEpoch { get; }
    public long CurrentEpoch { get; }
}
