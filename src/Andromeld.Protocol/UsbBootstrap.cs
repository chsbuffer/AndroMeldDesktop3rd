using System.Globalization;

namespace Andromeld.Protocol;

/// <summary>The shell-only provider contract shared by Web USB and the desktop ADB transport.</summary>
public sealed record UsbBootstrap(int WebPort, string Ticket)
{
    public const string ShellCommand = "content call --user 0 --uri content://com.catchingnow.andfiles.helper.webusb --method bootstrap";

    public static UsbBootstrap Parse(string output)
    {
        const string prefix = "Result: Bundle[{";
        string text = output.Trim();
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith("}]", StringComparison.Ordinal))
        {
            throw new IOException("无法获取连接信息，请确认手机已安装并激活 AndroMeld helper。");
        }

        Dictionary<string, string> fields = new(StringComparer.Ordinal);
        foreach (string entry in text.AsSpan(prefix.Length, text.Length - prefix.Length - 2).ToString().Split(", "))
        {
            int separator = entry.IndexOf('=');
            if (separator <= 0 || !fields.TryAdd(entry.Substring(0, separator), entry.Substring(separator + 1)))
            {
                throw new IOException("手机返回的连接信息格式无效。");
            }
        }

        if (!fields.TryGetValue("status", out string? status))
        {
            throw new IOException("手机未返回连接状态。");
        }

        if (status is not "ok")
        {
            fields.TryGetValue("errorCode", out string? error);
            throw new UsbBootstrapException(error ?? "invalid_response");
        }

        if (!fields.TryGetValue("responseVersion", out string? version) || version is not "2" ||
            !fields.TryGetValue("webPort", out string? portText) ||
            !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port) ||
            port < AndromeldEndpoint.DefaultWebPort || port >= AndromeldEndpoint.DefaultWebPort + AndromeldEndpoint.WebPortCount ||
            !fields.TryGetValue("ticket", out string? ticket) || !IsValidTicket(ticket))
        {
            throw new IOException("手机返回的连接信息不受支持，请更新 AndroMeld helper 后重试。");
        }

        return new UsbBootstrap(port, ticket);
    }

    internal static bool IsValidTicket(string ticket)
    {
        return ticket.Length == 8 && ticket.All(static c => "123456789ABCDEFGHJKMNPQRSTUVWXYZ".Contains(c));
    }
}