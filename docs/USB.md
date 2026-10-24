# USB 与地址连接

桌面使用 AdvancedSharpAdbClient → ADB forward → ClientWebSocket，复用同一 Web 主控、文件、终端和镜像协议。

## 设备页

主窗口竖屏；设备页按 USB、附近设备、IP 地址、连接码分区。局域网/直连 Web 无法区分手机的 Wi‑Fi/有线网络，统一标为“局域网”；经无线 ADB 转发时标为“无线调试”。连接码入口当前禁用，仅凭连接码连接需要 WebRTC。

## ADB 定位与分发

随包提供 Windows x64 / Linux x64 ADB。已有 server 直接复用；未运行时按 `ANDROID_HOME`（或 `ANDROID_SDK_ROOT`）/`platform-tools` → PATH → 程序同目录 → `runtimes/<当前 RID>/native/` 启动，不停止其它工具的 server。构建分发布局见 [BUILD](BUILD.md)。

## 连接流程

1. ADB 查找一次；插拔或授权后手动“重新查找”，无后台轮询/自动重连。
2. 重新确认 serial；用 `pm path` / `dumpsys package` 检查 helper；读取官网 `AndroMeld-WebUSB.apk.json`。
3. 未安装或版本低时下载 APK、校验 SHA-256、经 ADB Sync 复制并执行 `pm install -r -g`，安装后读回版本；相同或更高版本复用，不降级；生产代码不写死版本号。
4. 官网不可达但已有 helper 时继续尝试；元数据、哈希、安装或读回失败直接报错，不回退到旧安装。
5. bootstrap 返回隐私确认时，等用户在手机完成后点击“已完成，继续”；不自动接受条款。
6. bootstrap 成功后用 `forward:norebind:tcp:0;tcp:<webPort>` 分配本地端口，连接 `ws://127.0.0.1:<port>/ws`，显式设置允许的 Origin 并禁用代理。

Provider 命令：

```sh
content call --user 0 --uri content://com.catchingnow.andfiles.helper.webusb --method bootstrap
```

要求 `status=ok`、`responseVersion=2`、`webPort` 在 13012–13021、8 字符 base32 ticket；不要把 mDNS 原生 Agent 端口用于 WebSocket。仅 `agent_unavailable` 触发 agent 启动：安装/升级后通知 `agentwatch.plannedExit`、停止旧进程、清理 state，用 `CLASSPATH` + `app_process DeviceAgentMain` 启动，校验 PID 与 `/data/local/tmp/AndroMeldAgent/state.json` 一致，最多重试 bootstrap 六次（间隔 500ms）。

凭据按 USB serial 的 SHA256 单独保存，不与 LAN 端点混用；缓存和排序按认证后设备身份。正常断开、认证失败、初始化取消和模型释放均清理本次 forward，不调用 remove-all。

## 无线 ADB

Wi-Fi 连接卡片可切换二维码和 IP 地址。二维码使用 Android 的 `WIFI:T:ADB;S:<service-name>;P:<pairing-code>;;` 格式；每次生成临时服务名与密码，只匹配该服务名的 `_adb-tls-pairing._tcp`。配对成功后连接同地址的 `_adb-tls-connect._tcp` 端口，确认对应 serial 已 online，再复用 helper/bootstrap/forward。二维码密码不持久化；AndroMeld 凭据按 `adb:<serial 的 SHA256>` 保存，与手动 IP 连接分开。

设备页的 IP 地址连接无需端口：先按 IP 探测 13012–13021 的 Web 服务；没有 Web 服务时，通过 ADB server 的 mDNS 连接无线调试，随后复用上面的 helper/bootstrap/forward 流程；区别只在 ADB 设备的 serial 是 `ip:port`，以及凭据按 `wireless:<IP 的 SHA256>` 保存。无线调试凭据与 USB serial、LAN 端点分开保存。

1. 用 `host:mdns:services` 取得 ADB server 已发现的 `_adb-tls-connect._tcp` / `_adb-tls-pairing._tcp`，按输入 IP 过滤。首次查询为空时先试经典 5555；若仍失败，再轮询 mDNS 约 5 秒，避免 ADB server 的 mDNS 缓存尚未预热。
2. `host:connect:<ip>:<port>`。返回 `failed to authenticate` 时进入配对：请求用户输入手机显示的 6 位配对码，最多轮询 `_adb-tls-pairing._tcp` 45 秒，然后执行 `host:pair:<code>:<ip>:<port>` 并重新连接。配对码只用于本次调用，不写设置或日志。
3. 连接成功后等待 `host:devices-l` 中的设备变为 online；`unauthorized` 时提示用户在手机上允许调试，最多等待 60 秒，可随时取消。
4. 之后与 USB 相同：检查/安装/升级 helper、准备 agent、bootstrap、`forward:norebind:tcp:0`、Origin + 无代理的 WebSocket。缓存和排序仍按认证后设备身份。

不调用 `adb disconnect`，避免影响 Android Studio 等共用同一 ADB server 的工具；只清理本次 forward。无线调试的 connect/pairing 端口在每次开关无线调试后变化，由 mDNS 重新发现；配对关系保存在手机和 ADB host key 中，手机 IP 变化后需要重新配对。经典 5555 需要手机已通过 `adb tcpip 5555` 或厂商“网络 ADB”开关开启，程序不会自行切换 USB 设备的 TCP 模式。mDNS 多播需手机与电脑在同一网络且未被 AP 客户端隔离；受限网络下只能使用 Web 服务直连或 USB。mDNS 只保留每个接口的路由，移除一条不删其余；Tmds.MDns 当前用主动查询/goodbye/超时驱动，不是严格 RFC TTL 缓存。

## 坑点

Android shell 命令必须 LF。`.gitattributes` 默认 LF，仅 `.bat`/`.cmd` 使用 CRLF；不要把多行命令写成受 Windows 换行影响的 C# raw string。模拟 ADB 也拒绝多行 CRLF 命令。

| 操作 | 超时 |
| --- | --- |
| 查找设备 | 10 秒 |
| 元数据/普通命令 | 每项 15 秒 |
| 下载/复制/安装 | 每项 120 秒 |
| agent 停止/启动与 state 校验 | 共 20 秒 |
| 隐私等待 | 无自动超时 |
| forward 分配/清理 | 5 秒 / 每项 3 秒 |
| mDNS 查询 / 连接服务预热 | 每项 3 秒 / 共 5 秒 |
| 等待配对服务 / 等待设备 online | 45 秒 / 60 秒 |
