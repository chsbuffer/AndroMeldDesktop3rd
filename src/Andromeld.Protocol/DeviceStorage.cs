namespace Andromeld.Protocol;

/// <param name="Total">Total bytes, using Android StatFs.getTotalBytes.</param>
/// <param name="Free">Available bytes, using Android StatFs.getAvailableBytes.</param>
public sealed record DeviceStorage(long? Total, long? Free);