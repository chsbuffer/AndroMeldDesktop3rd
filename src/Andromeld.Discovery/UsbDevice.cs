namespace Andromeld.Discovery;

public sealed record UsbDevice(string Serial, string Name, UsbDeviceState State, bool IsWireless = false);
