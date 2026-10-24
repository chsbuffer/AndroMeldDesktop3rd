# 文本剪贴板同步

- 设置页可开关文本剪贴板同步，默认开启；每 500ms 比较剪贴板，软件窗口失焦时暂停，正文不落盘、不记日志。
- 协议：`[3,3]+{on}` 订阅，`[3,2]+{text}` 电脑→手机，`[3,1]+{text}` 手机→电脑；空文本不上传。详见 [PROTOCOL](PROTOCOL.md)。
- 桌面使用 Avalonia `IClipboard`；Linux Wayland 会话使用原生 Wayland，不需要 `wl-clipboard`；仅在软件窗口活动时同步。
- 主窗口、手机/应用镜像、文件和终端窗口在 `IsActive` 属性变更后交接活动剪贴板；Avalonia 的 `Activated` 早于 `IsActive` 更新，不能在该事件中读取最终焦点状态。窗口协调和剪贴板服务都归 UI 线程。
- 剪贴板只同步文本，不承诺远程文件：手机文件复制/粘贴不实现，文件操作见 [FILE_BROWSER](FILE_BROWSER.md)。
