namespace Andromeld.Protocol;

public sealed class AuthenticationDeniedException(string reason) : IOException(reason switch
{
    "UNAUTHORIZED" => "凭据无效或已过期，请重新配对。",
    "DENIED" => "手机拒绝了配对请求。",
    "TIMEOUT" => "配对超时，请在手机上确认后重试。",
    "BUSY" => "手机正在处理另一项配对，请稍后重试。",
    _ => $"手机拒绝连接：{reason}"
})
{
    public string Reason { get; } = reason;
}