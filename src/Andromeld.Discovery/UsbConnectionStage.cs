namespace Andromeld.Discovery;

public enum UsbConnectionStage
{
    CheckingDevice,
    CheckingHelper,
    CheckingUpdates,
    Downloading,
    Copying,
    Installing,
    PreparingService,
    WaitingForPrivacyConsent,
    Forwarding,
    FindingWirelessService,
    ConnectingWireless,
    WaitingForWirelessAuthorization,
    WaitingForWirelessPairing,
    FindingWirelessPairing,
    PairingWireless
}
