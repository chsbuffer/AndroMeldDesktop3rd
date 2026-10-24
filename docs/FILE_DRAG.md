# 文件双向拖拽

文件列表和应用镜像可接收电脑文件；Android 应用实际发起拖动后才可拖出，要求 Android 16+、支持跨应用拖放的源应用和可用 helper bridge，普通长按不伪造文件。上传通过 imported 标记确认，未接收时提示保存到手机下载目录。仅支持文件，文件夹拒绝。

## 协议

- 上传：主控 channel 5，type 1 JSON begin（id/name/size/mime，互斥 destPath/sessionId）、type 2 BE32 id+正文、type 3 JSON end、type 4 abort。最大 4 GiB，最多 4 个在途上传，每块 64 KiB，等待 upload-done/upload-failed；变长/变短/取消/失败都发 abort。没有 `c:"fs-upload"`。
- 拖出：复用 `/media`，drag-out live → handoff(x/y) → metadata → transfer-start/chunk-ack → item-done → terminal；txn 原样保留十进制；`[5,5]` 为 29 字节大端头 + ≤256 KiB 正文。接收后核对项目、请求、实际大小、声明大小和 SHA-256，空文件也校验；错误不关闭设备连接；35 秒无进展超时。
- 上传入口在 channel 5；不要按通道名把 bulk 当文件通道。

## 线程与清理

UI 拥有手势、目录快照和取消；Android live 交接后冻结 pointer。Windows OLE 在源 HWND 的 STA/UI 线程执行，只提供 COPY；目标请求内容时才接收，GetData 可同步等待独立网络 worker。临时文件用随机目录和 `.part`，校验后改名；结束/取消等待 worker 并清理。目标长期持有流时 Windows 可能阻止删除，崩溃残留尚无启动清理。

Linux 文件拖出仅支持 KDE Wayland + Dolphin/KIO/Ark。平台层创建临时 D-Bus 对象并提供 `application/x-kde-ark-dndextract-service` / `application/x-kde-ark-dndextract-path`；Dolphin 回调目标目录后立即 ACK，worker 直接写入目标目录的 `.andromeld-*.part`，完成后按唯一名称原子改名。该路径不生成 `file://` 临时文件，也不回退到 X11、XWayland 或非 KDE Wayland；不支持的会话在启动拖动前报错。
