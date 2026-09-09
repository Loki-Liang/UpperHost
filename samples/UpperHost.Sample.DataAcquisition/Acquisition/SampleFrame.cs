namespace UpperHost.Sample.DataAcquisition.Acquisition;

public sealed record SampleFrame(
    long Sequence,
    DateTimeOffset Timestamp,
    double[] Channels);
