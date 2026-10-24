using System.Text.Json.Serialization;

namespace Andromeld.Protocol;

public sealed record MirrorState(
    string SessionId,
    string State,
    [property: JsonPropertyName("pkg")] string? PackageName,
    string? Kind = null,
    string? Reason = null,
    int UserId = 0)
{
    [JsonIgnore]
    public bool IsPhone => string.Equals(Kind, "phone", StringComparison.Ordinal);

    public static string FailureMessage(string? reason)
    {
        return reason switch
        {
            "pool-full" => "手机的镜像窗口已满，请先关闭一个窗口。",
            "start-failed" => "手机未能启动镜像，请检查 AndroMeld 服务和授权。",
            "invalid-intent-url" => "手机不支持此链接，请使用 http、https、tel、mailto 或 geo 链接。",
            _ => $"镜像会话已结束：{reason ?? "未知原因"}"
        };
    }
}