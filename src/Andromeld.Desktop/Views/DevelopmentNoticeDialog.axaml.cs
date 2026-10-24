using FluentAvalonia.UI.Controls;

namespace Andromeld.Desktop.Views;

public partial class DevelopmentNoticeDialog : FAContentDialog
{
    protected override Type StyleKeyOverride => typeof(FAContentDialog);

    public DevelopmentNoticeDialog() => InitializeComponent();
}
