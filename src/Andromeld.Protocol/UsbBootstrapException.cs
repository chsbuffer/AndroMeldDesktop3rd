namespace Andromeld.Protocol;

/// <summary>Retains the provider error code without retaining its ticket/code-bearing output.</summary>
public sealed class UsbBootstrapException(string errorCode) : IOException(errorCode switch
{
    "privacy_consent_required" => "请在手机完成 AndroMeld 隐私确认。",
    "agent_unavailable" => "手机 AndroMeld 服务尚未就绪，请重试。",
    "web_service_unavailable" => "手机 Web 服务无法启动，请在手机上检查 AndroMeld。",
    "ticket_unavailable" or "ticket_invalid" => "手机无法提供有效的连接票据，请重试。",
    "web_version_unavailable" or "unknown_method" => "手机 helper 不支持此连接接口，请更新后重试。",
    _ => "手机暂时无法准备连接，请检查 AndroMeld 服务后重试。"
})
{
    public string ErrorCode { get; } = errorCode;
}