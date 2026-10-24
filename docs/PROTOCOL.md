# 已实现的 LAN 协议

依据实际协议行为整理；桌面使用 Web 入口；RTC/Mac 原生协议未接入。USB 复用同一 WebSocket 协议。

## 入口与认证

- 手机 helper 有原生 TLS Agent（通常 12969–12979）和 LAN WebSocket（通常 13012–13021）；mDNS `_andromeld._tcp` 公告的是原生 Agent 端口，不是 Web 端口。
- Web 主控 `/ws`，Origin 为 `https://app.andromeld.catchingnow.com`；v2 pair/bootstrap/resume，控制帧 `[4,1]+UTF8(JSON)`，文件 channel 5，终端数据 `[6,1]`。
- 认证拒绝保留 deny.reason（UNAUTHORIZED/DENIED/TIMEOUT/BUSY/未知）。resume+UNAUTHORIZED 时先取消并等待当前 client，再释放原 socket、移除端点旧凭据，并用同一 clientId 新建 socket pair 一次；pair 被拒绝或其它错误不自动重试。新 token 只在 welcome 成功后保存。
- 连接码入口当前禁用，仅输入连接码的 WebRTC 传输待实现。JSON 只用于 c/reqId 分发，设备详情等固定数据交给类型化 record。

## 主控、应用与文件

- `open-window` 的 `pkg`+`userId`+`kind` 与 `intentUrl` 互斥；intentUrl 只允许 http(s)、tel、mailto、geo 及合法裸域名，失败返回无包名的 `window-state.failed`。`AndroidIntentUri` 和状态关联归 Protocol。
- `device-info` 无 reqId，响应含 device/battery/storage；battery.level 为整数百分比，storage 来自外部存储 StatFs，读取失败省略。桌面在连接成功/打开设备页时请求；缺失或越界显示未知。
- `app-list` 返回列表；`app-icons` 每批最多 16 个 `{packageName,userId}`，PNG base64。列表不要求镜像会话。
- `fs-roots/fs-list/fs-stat/fs-download` 用非空 reqId（≤64 字符）关联；目录通常每页 200，nextCursor 为字符串，mtime 为 Unix 毫秒；只允许 canonical external storage root。
- `fs-delete` 使用 `paths: string[]`（1–64 个）并返回 `fs-op-result { reqId, op:"delete", ok:true }`；文件夹递归永久删除。桌面按 `FileOperationResult` DTO 反序列化并校验 `c/op/ok`，错误走 `err`。
- 文件 channel 5：type 6 begin、7 chunk、8 end、9 abort、10 ACK；chunk 为 u32be transferId+bytes；ACK `{id,received}` 为累计写入字节。先写本地再 ACK，完整接收才替换目标；每连接一项活动下载，队列最多 64，块 256 KiB，窗口 512 KiB，等待 30 秒。

## 镜像、显示与输入

- 主控负责 open-window、会话状态和 ACK；每个窗口独占 `/media` attach 与输入。视频头解析、关键帧/gap/uint64 ACK 在 Protocol；Annex B H.264 由 [Media](MEDIA.md) 解码。
- 应用尺寸：真实视频区域 DIP × RenderScaling，转偶像素、最小 240、最长边 1920，并按缩放调整 dpr；首次创建和 resize 共用路径，180ms 防抖，resize 后请求关键帧。手机镜像只本地等比显示。
- display：zoom 50%–150%、步长 10%，发 `[4,1]+{c:"density",scale}`；普通宽视口 100% 对应 scale=0.8；APK 用 `round(160*dpr*scale)` 并限 16–640 DPI。兼容模式按 392 DIP 短边重算 w/h，没有 compat 命令。配置顺序 density → resize → 变化时的 decor-insets → 关键帧；`decor-insets.top` 为标题栏 DIP × 有效 DPR，非沉浸和全屏为 0。
- 鼠标：未按压移动发 `pointer/hover`；右键用 pointer down/up 模拟至少 600ms 长按；只有“右键返回”开启时才发 Back。修饰键只用于本地匹配，不附加到 Android 导航键。
- 导航键：电源 26、最近任务 187、主页 3、返回 4；`input.k=key`，`meta=0`。
- **普通应用 `task-state.away` 不是关闭通知。** away 持续 850ms（启动阶段 3 秒）后隐藏视频并显示“重新启动”，away=false 恢复；只有主控 `window-state.closed` 关闭窗口，failed 显示错误。手机镜像忽略应用 away。
- 游戏使用完整可用屏幕区域，等实际视口和 DPI 后 open；线路没有 orientation 字段，P/L/W 是 Web 布局状态，不是 Android 旋转指令。

## 屏幕摄像头

- 媒体 `[4,1]+{c:"fusion-camera",on:true|false}` 通知相机需求；桌面经同一 `/media` 发 `[7,1]+JPEG`（2–1 MiB，FF D8 开头，无 JSON/base64/宽高/帧号）。头或大小错误会关闭媒体连接；未开启或无取帧请求时丢弃合法帧。
- helper 用 VirtualDeviceManager + `POLICY_TYPE_CAMERA=DEVICE_POLICY_CUSTOM` 注册前后虚拟镜头，仅 own-content 应用挂接；Android 16+、VDM/CDM/ROM/权限任一失败都会导致相机不可用，镜像成功不等于相机可用。
- attach 可重放 on 状态；off、断线、关闭停止供图。`fusion-camera` 是服务端通知，客户端不能反向发送它开启相机。JPEG 由 BitmapFactory 解码后画到 VirtualCamera Surface。

## 文件拖出

- `/media` `[4,1]`：桌面发 drag-out-handoff、transfer-start、chunk-ack、terminal；Android 用 `c:"drag-out"` 的 capability/ready/live/handoff-ack/metadata/transfer-started/item-done/terminal 回复；txn 保留原十进制字符串。
- `[5,5]` 文件块：29 字节大端头（i64 txn、i64 req、i32 item、i64 offset、u8 isFinal）+ ≤256 KiB 正文；写后发累计 offset ACK；EOF 的 item-done 校验实际大小和 SHA-256，不能只看 isFinal。
- 要求 Android 16+、支持跨应用拖放的源应用和 helper bridge 实际就绪；普通长按不伪造文件。

## 剪贴板与终端

- 剪贴板：`[3,3]+{on}` 订阅，`[3,2]+{text}` 电脑→手机，`[3,1]+{text}` 手机→电脑；空文本不上传，正文不记日志、不落盘。
- 终端：`[4,1]` term-open/opened/resize/ack/close/exit；数据 `[6,1]+u32be id+PTY bytes`。每连接最多 4 PTY；cols 1–500、rows 1–300；每新增 32 KiB 累计 ACK，APK 在未确认输出达 128 KiB 时暂停；关闭和迟到 opened 需要清理。

## 通知同步

- 已核对通知模型、订阅和跳转行为：主控 `[4,1]` 发送 `notif-subscribe {reqId,on}`，以 `notif-state {reqId,on,err}` 确认；需要 welcome 的 `notification` capability 和手机通知使用权。
- `notif-snapshot` 是完整快照，含 `active/hidden/recent`；仅 active、未移除且非 systemHidden 的通知产生桌面提醒。首快照只建立基线，与 Web 的初次提醒行为一致；后续按完整 Android `key` 去重、更新和撤回，仅标题、显示正文或按钮变化时更新系统通知，时间戳及不可见元数据变化不重复提醒。正文不记录日志、不持久化。
- `notif-cmd {reqId,command:2,key}` 清除手机通知；`command:6` 执行内容 Intent，activity/unknown/none 先打开对应 Android 用户的应用镜像，再附 `sessionId/fallbackPkg/fallbackUserId`。响应 `notif-cmd-result` 按 reqId 关联，权限/命令错误不关闭主控连接。
- Windows 内联回复使用快照 `actions` 中第一个 `hasRemoteInput:true` 动作，保留其原始 `index`，不使用数组位置。`notif-cmd {reqId,command:8,key,actionIndex,replyText,sessionId}` 发送回复；与 Web 一致，回复总是先打开/复用对应包名和 Android 用户的镜像会话，包括 broadcast/service Intent。文本去除首尾空白，并限制为 4096 个 UTF-16 单元；空白回复不发送，不记录或持久化正文。APK 校验 actionIndex 为 0–64、key 最长 512、sessionId 最长 64；类型化 API 在发送前校验。

## Android c 索引

客户端实现覆盖主控、媒体和原生 widgets 三组 c 消息；`widget-detach` 为 helper 内部专用；`widget-picker/config` 属原生 widgets 服务；`fusion-camera/microphone` 是媒体状态通知；上传入口在 channel 5，没有 `c:"fs-upload"`。
