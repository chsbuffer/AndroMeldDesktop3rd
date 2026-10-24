# 通知同步

Linux/Windows 通过 Avalonia.Labs.Notifications 的通知接口接入，依赖版本以桌面 `csproj` 为准。手机协议见 [PROTOCOL](PROTOCOL.md)。

- Windows 使用库的 WinRT 后端，标题、正文和回复提示按 XML 转义。
- Linux 因库的旧 Tmds ABI 与 Avalonia 要求的版本不兼容，注册兼容的 freedesktop Notifications 后端；系统服务不支持操作按钮时，交互能力由桌面环境决定。
- Windows 通过 `ReplyActionTag` 和 `NotificationCompleted.UserData` 接收内联文本；更新通知撤销回复动作时移除输入框，镜像启动期间动作变更、断线或关闭同步时取消发送。Linux 后端不支持文本输入，保留打开/清除操作。
- Linux 对同一 ID 的排队更新合并为最新内容，D-Bus Notify 间隔至少 1.1 秒；Plasma 对一秒内相同内容返回 `ExcessNotificationGeneration`，即使 `replaces_id` 非零也会拒绝（见 [KDE ServerPrivate::Notify](https://github.com/KDE/plasma-workspace/blob/master/libnotificationmanager/server_p.cpp)）。临时限流延迟重试，最多三次尝试，权限等其他错误正常报告；撤回或关闭同步后跳过尚未发送的更新。
