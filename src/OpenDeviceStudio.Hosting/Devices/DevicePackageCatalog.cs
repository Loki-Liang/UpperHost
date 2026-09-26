using OpenDeviceStudio.Abstractions.Devices;

namespace OpenDeviceStudio.Hosting.Devices;

internal sealed class DevicePackageCatalog : IDevicePackageCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DevicePackageDescriptor> _descriptors =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<DevicePackageDescriptor> Descriptors
    {
        get
        {
            lock (_sync)
            {
                return _descriptors.Values
                    .OrderBy(descriptor => descriptor.PackageId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
        }
    }

    public DevicePackageRegistrationResult Register(DevicePackageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateDescriptor(descriptor);

        lock (_sync)
        {
            if (!_descriptors.TryGetValue(descriptor.PackageId, out var existing))
            {
                _descriptors.Add(descriptor.PackageId, descriptor);
                return new DevicePackageRegistrationResult(
                    DevicePackageRegistrationOutcome.Added,
                    descriptor);
            }

            var comparison = descriptor.PackageVersion.CompareTo(existing.PackageVersion);
            if (comparison > 0)
            {
                _descriptors[descriptor.PackageId] = descriptor;
                return new DevicePackageRegistrationResult(
                    DevicePackageRegistrationOutcome.ReplacedOlderVersion,
                    descriptor);
            }

            if (comparison < 0)
            {
                return new DevicePackageRegistrationResult(
                    DevicePackageRegistrationOutcome.IgnoredOlderVersion,
                    existing);
            }

            if (ReferenceEquals(existing, descriptor))
            {
                return new DevicePackageRegistrationResult(
                    DevicePackageRegistrationOutcome.Unchanged,
                    existing);
            }

            throw new DevicePackageConflictException(
                descriptor.PackageId,
                descriptor.PackageVersion);
        }
    }

    public bool TryGet(string packageId, out DevicePackageDescriptor? descriptor)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            descriptor = null;
            return false;
        }

        lock (_sync)
            return _descriptors.TryGetValue(packageId, out descriptor);
    }

    public IReadOnlyCollection<DevicePackageDescriptor> Query(DevicePackageQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_sync)
        {
            IEnumerable<DevicePackageDescriptor> result = _descriptors.Values;

            if (!string.IsNullOrWhiteSpace(query.Capability))
            {
                result = result.Where(
                    descriptor => descriptor.CapabilityTypes.Contains(
                        query.Capability,
                        StringComparer.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(query.Transport))
            {
                result = result.Where(
                    descriptor => descriptor.TransportTypes.Contains(
                        query.Transport,
                        StringComparer.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(query.Vendor))
            {
                result = result.Where(
                    descriptor => string.Equals(
                        descriptor.Vendor,
                        query.Vendor,
                        StringComparison.OrdinalIgnoreCase));
            }

            return result
                .OrderBy(descriptor => descriptor.PackageId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private static void ValidateDescriptor(DevicePackageDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(descriptor.PackageId))
            throw new ArgumentException("Device package id is required.", nameof(descriptor));
        if (descriptor.PackageVersion is null)
            throw new ArgumentException("Device package version is required.", nameof(descriptor));
        if (string.IsNullOrWhiteSpace(descriptor.DeviceId))
            throw new ArgumentException("Device id is required.", nameof(descriptor));
        if (string.IsNullOrWhiteSpace(descriptor.DisplayName))
            throw new ArgumentException("Device display name is required.", nameof(descriptor));
        if (string.IsNullOrWhiteSpace(descriptor.ConfigurationSchema.Version))
            throw new ArgumentException("Configuration schema version is required.", nameof(descriptor));
    }
}
