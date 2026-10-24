# 架构与线程归属

七个生产项目：Protocol、Media、Discovery、Cache、Platform.Windows、Platform.Linux、Desktop；前六者不依赖 Avalonia。协议/解码/发现/缓存各有测试项目。设计原则是先确定线程所有者，不靠 Lock 掩盖归属。功能级实现约束见各子系统文档。

## 项目边界

- **Protocol**：DTO、请求/响应、帧、认证、端点解析（`AndromeldEndpoint`）、终端/镜像/剪贴板/文件拖拽状态机；无 UI/平台依赖。新增响应使用 typed record/DTO 反序列化，不用 `TryGetProperty` 链手工探测字段。
- **Discovery**：发现记录、mDNS、LAN 设备归并、Web 端口确认、USB 发现/准备/forward，以及 IP 直连失败后的无线 ADB（ADB server mDNS、配对、online 等待）；每次操作独占 AdvancedSharpAdbClient，串行确认 serial、helper、agent 和 bootstrap，无线连接在设备 online 后复用同一 helper/agent/bootstrap/forward 流程。Protocol 提供官网元数据、安装/服务命令、版本/PID/state 和 bootstrap 错误码。
- **Cache**：EF Core SQLite；可并发，每次操作独立 DbContext/事务，输入集合在完成前不得修改；SQLite 同步 I/O 不在 UI 线程。
- **Platform.Windows**：CsWin32 生成的 Win32/COM 封装；Desktop 只传 HWND/托管数据。
- **Platform.Linux**：KDE 会话识别、Tmds D-Bus 连接、KIO/Ark 拖放服务及 MIME/接口常量；不依赖 Avalonia、Desktop 或手机 Protocol。D-Bus 请求通过注入的 `SynchronizationContext` 交回所属线程。
- **Desktop**：Avalonia 控件、终端适配器、用户设置、ReactiveUI 和平台接入。保留 Avalonia 拖动手势和 `DataFormat` 适配，不直接构造 D-Bus 服务。

平台启动集中在 DesktopPlatform：先用 `UsePlatformDetect()` 初始化平台、渲染和文字，Linux 检测到 `WAYLAND_DISPLAY` 时再切换到 `UseWayland()`。功能级实现见 [MEDIA](MEDIA.md)、[MIRROR_WINDOWS](MIRROR_WINDOWS.md)、[FILE_DRAG](FILE_DRAG.md) 等子系统文档。

## 线程与生命周期

| 类型 | 规则 |
| --- | --- |
| MainViewModel / NearbyDeviceViewModel | UI 线程；集合、属性和命令在 UI 修改；LAN 认证恢复见 [PROTOCOL](PROTOCOL.md)。 |
| 设备详情 | Protocol 返回不可变快照；MainViewModel 在 UI 串行请求，await 后核对 client 身份、连接状态和取消令牌，迟到结果不能填回。 |
| MainWindow / FileBrowserWindow | UI 线程；主窗口协调镜像和单例文件窗口；文件窗口关闭只取消文件操作，不释放共享连接。文件浏览状态在 window-scoped `FileBrowserViewModel`，`MainViewModel` 只保留连接 attach/detach 和窗口入口。 |
| FileBrowserViewModel | UI 线程；拥有 roots、路径/历史、`SelectionModel<FileBrowserItem>`、树缓存、下载/上传/删除和拖出批次；关闭窗口 `Suspend`，重开 `Resume`，断开 `Detach`。 |
| ILanDiscovery / DiscoveryCatalog | 构造、Start、Refresh、Devices、Dispose 同一线程；Tmds 事件投递到该线程并 VerifyAccess。 |
| UsbDiscovery / UsbPreparation | 每次查找/连接独占 ADB client；HttpClient 可注入且不由操作释放；Sync 在 worker 构造，取消关闭专属 socket 并等待退出；forward 用 tcp:0/norebind，lease 串行释放。WirelessAdbConnector 同样一次操作一个 client；mDNS/配对在 worker，配对码只在内存回调中传递，进度投递 UI。 |
| 无线调试二维码配对 | Protocol 生成临时二维码凭据；Discovery 每次配对独占 ADB client/socket。UI 所有选择、状态和取消；切换页面/连接方式、重新生成或关闭时取消并等待旧任务，迟到结果不得连接；认证成功后连接由工作区接管。 |
| AppCache | 可并发；每次操作在线程池创建独立 DbContext 和事务；返回快照属于调用者。 |
| MirrorViewModel / MirrorWindow | UI 拥有几何、ViewportSize、宽限定时器和释放；协议通知先投递 UI，再读 SessionId/释放状态。 |
| TerminalViewModel / TerminalSession | UI 拥有命令、会话、尺寸和释放；主控 reader 把 PTY 字节投到 256 KiB channel，不等待 UI；一个消费者解析后 ACK；resize 泵和 Dispose 属于 UI。 |
| 解码/渲染 | FFmpeg worker 拥有解码器；帧合并投递 UI。SDL 创建、纹理更新、渲染和销毁全在 UI，输入走 Avalonia 路由。 |
| ClipboardSharing | UI 所有；协议文本投递 UI，一个 async 操作串行平台读写和发送，revision 拒绝过时读取。 |
| NotificationSharing / LinuxNotificationManager | UI 所有；协议快照和系统回调投递 UI，原生操作串行。共享泵等待订阅切换；关闭/断线立即取消，异步结果按连接令牌和通知身份拒绝迟到操作。Windows 回复先由 VM 确保对应包名和 Android 用户的镜像会话；Linux 队列、合并和限流见 [NOTIFICATIONS](NOTIFICATIONS.md)。 |
| 音频 | MainViewModel 在 UI 串行协调开关、重连和镜像所有者；Protocol reader 向有界 channel 非阻塞投递带配置的 PCM；AudioSharing/SDL 的 tick、初始化、重配、播放和释放均属 UI。 |
| 文件拖放 | UI 拥有手势和目录快照；Protocol reader 生产容量 8 的拖出队列，单个 worker 写入、校验、ACK；Windows OLE/COM 回调在 HWND 的 STA 线程执行，可等待不依赖 UI 的 worker；KDE 回调由 D-Bus 读取后 Post 回注入的 UI 上下文，服务状态也只在该上下文读写；上传每连接四个槽位。 |
| KdeArkDropService | 构造/释放和 `_disposed`/`_received`/目标 TCS 属于注入的 owner context；`HandleMethodAsync` 只解析请求和 Post，不读可变服务状态；D-Bus 回复在 owner context 执行，连接释放后的迟到回调只做 best-effort 错误回复。 |
| ScreenCameraSharing | UI 拥有需求、开关、异步授权、计时器、平台会话和 Windows 物理视口/affinity；worker 串行拥有原生帧/JPEG 资源，GStreamer 拥有内部流线程；Portal D-Bus 回调只完成 TCS；15fps、单帧在途；停止先取消并等待 worker，再释放来源/会话；需要时延迟 native close。 |
| MirrorSession 关闭 | 外部 owner 异步 Dispose：先禁止新 JPEG，保持媒体 reader；发送 close-window 后等待该 session 的 closed/failed，最多 3 秒；随后取消并 Abort 媒体；远端已关闭则跳过。 |
| 窗口身份 | HWND 创建线程独占；AppUserModelID 每窗口独立；subclass 在 WM_NCDESTROY 解除；宿主 Dispose 只验证 owner 线程，不访问已销毁 HWND。 |
