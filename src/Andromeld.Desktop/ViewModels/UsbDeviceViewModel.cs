using System.Windows.Input;

using Andromeld.Discovery;

using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class UsbDeviceViewModel(UsbDevice device, ICommand connectCommand, ICommand rePairCommand) : ViewModelBase
{
    public ICommand ConnectCommand { get; } = connectCommand;
    public ICommand RePairCommand { get; } = rePairCommand;
    [Reactive(nameof(Name), nameof(Serial), nameof(IsAvailable), nameof(ConnectionHint))]
    public partial UsbDevice Device { get; private set; } = device;
    [Reactive(nameof(ConnectionHint))]
    public partial bool IsConnecting { get; internal set; }
    public string Name => Device.Name;
    public string Serial => Device.Serial;
    public bool IsAvailable => Device.State == UsbDeviceState.Online;
    public string ConnectionHint => IsConnecting
        ? "正在连接…"
        : Device.State switch
        {
            UsbDeviceState.Online => "USB · 点击连接",
            UsbDeviceState.Unauthorized => "请解锁手机并允许 USB 调试，然后重新查找",
            _ => "设备未就绪，请检查 USB 连接后重新查找"
        };

    public void Update(UsbDevice device)
    {
        Device = device;
    }
}