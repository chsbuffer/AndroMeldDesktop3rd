# 终端

- 终端使用独立窗口，复用认证后的 `/ws`，不创建镜像会话。命令 `term-open/opened/resize/ack/close/exit`；数据 `[6,1]+u32be id+raw UTF-8`。
- 每连接最多 4 PTY；cols 1–500、rows 1–300，非法尺寸不发给服务端。
- ACK 按解析后的原始字节每新增 32768 累计 `received`，排除帧头；不能在 socket 收到、转字符串或 `OutputReceived` 时确认。APK 未确认输出达 131072 字节时暂停；客户端队列 256 KiB，超量只结束对应 PTY。
- 取消/超时打开的 reqId 保留到响应或断开，迟到成功自动 term-close；term-close 无 ACK，不等待 term-exit。
- term-exit 先完成远端状态，再排空输出；控件读到 EOF 后启用重开。重开清空旧缓冲和字节计数，使用新 id；断线终止全部 PTY，旧窗口保留内容，不复用 id。
- 控件 Iciclecreek.Avalonia.Terminal（MIT，版本见 `csproj`），`Process=""`，只 AttachConnection 到远端，不启动本地 Shell。5000 行缓冲和 ANSI/VT 解析由控件负责。
- 控件每次读取后同步 `ConsumeOutputChunk → Terminal.Write`，下一次 `ReadAsync` 开始作为前一块已解析边界；升级时必须重新核对该契约。
- Windows 上控件会把普通 Esc 转成 ConPTY 输入记录；窗口隧道只在普通终端模式把 Esc 转成 `0x1B`，Kitty/Win32 已协商模式仍交给模拟器。Esc 不触发 Android 返回。
- UI 拥有窗口、模型、resize 合并泵和释放；控件拥有可取消输出 reader 和串行输入 writer；外部连接由模型 term-close，控件只解挂。退出后提示 Ctrl+D 关闭、Enter 重开；断开后只提供关闭。Ctrl+C 有选区时复制，否则中断。
