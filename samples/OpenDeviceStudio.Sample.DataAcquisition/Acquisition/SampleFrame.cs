namespace OpenDeviceStudio.Sample.DataAcquisition.Acquisition;

public sealed record SampleFrame(
    long Sequence,
    DateTimeOffset Timestamp,
    double[] Channels);
