using System.Windows.Input;

using Andromeld.Discovery;

using ReactiveUI.SourceGenerators;

namespace Andromeld.Desktop.ViewModels;

public sealed partial class NearbyDeviceViewModel(LanDevice device, ICommand connectCommand, ICommand rePairCommand) : ViewModelBase
{
    public ICommand ConnectCommand { get; } = connectCommand;
    public ICommand RePairCommand { get; } = rePairCommand;

    [Reactive(nameof(Name), nameof(Addresses))]
    public partial LanDevice Device { get; private set; } = device;

    public string Name => Device.Name;
    public string Addresses => string.Join(" · ", Device.Endpoints.Select(static ep => ep.Address.ToString()).Distinct(StringComparer.Ordinal));

    [Reactive(nameof(ConnectionHint))]
    public partial bool IsConnecting { get; internal set; }
    public string ConnectionHint => IsConnecting ? "正在连接…" : "点击连接";

    public void Update(LanDevice device)
    {
        Device = device;
    }
}