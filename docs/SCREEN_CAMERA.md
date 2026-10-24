# 桌面屏幕摄像头

应用镜像三点菜单的“屏幕摄像头”把桌面画面提供给 Android 应用相机。Windows 采集镜像视口背后的桌面；Wayland 通过系统选择器共享一个屏幕或窗口。每窗口默认关闭，仅在 Android 相机需求 on 时申请取屏并发送 JPEG。Android 16+ own-content 镜像、VDM/CDM/ROM 支持和 VirtualCamera 实际创建成功是前提，镜像成功不等于相机可用。

## 协议与图像

- `/media` 接收 `04 01 + {c:"fusion-camera",on:true|false}`；Desktop 发同一会话的 `07 01 + JPEG`，正文 2–1,048,576 字节，FF D8 开头，无 JSON/base64/宽高/frameId。
- 发送前检查需求和大小；错误帧不关闭合法连接；发送与其他媒体控制共用串行 writer。未收到 on 不发图，也不反向发送 fusion-camera。
- 采集和 JPEG 编码在 worker，最大 15fps，最多一帧在途，忙时跳过 tick。来源等比放入正向 720×1280 画布（黑边填充、不裁边），再逆时针旋转为 1280×720，quality 60，补偿 helper 的 90° 后摄声明；不依赖 EXIF。
- 最小化暂停发送图像；Wayland 的共享会话仍存在，系统撤销共享会立即触发清理。
- helper 对前后摄都声明 `sensorOrientation=90`、流规格 1280×720，且不旋转 JPEG；Web JS 也不做 ±90° 补偿。
- 镜头尺寸/方向由隐藏的 SystemApi `VirtualCameraConfig` 创建，不在 Web 协议暴露；改变 JPEG 大小不会改变注册规格，需要改手机 helper 并重建镜头。

## Windows 取景

- 需要 Windows 10 2004+。GDI 来源跟随窗口移动、缩放和 DPI，排除镜像自身；重新显示后使用最新几何。
- 供图期间窗口设为 WDA_EXCLUDEFROMCAPTURE，Windows 系统截图会看不到镜像；需要系统截图时先关闭摄像头。可用三点菜单“截图”保存原始应用帧（不含标题栏）。
- 鼠标闪烁的可疑路径是 StretchBlt 的 CAPTUREBLT；当前只用 SRCCOPY。

## Wayland 取景

- 使用标准 [XDG ScreenCast Portal](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.ScreenCast.html)，不限定 KDE；桌面需要支持 ScreenCast 的 Portal 后端、PipeWire，以及 GStreamer 的核心、app、videorate、videoconvert、videoscale 和 pipewire 插件（常见包为 GStreamer、gst-plugins-base、gst-plugin-pipewire，具体名称随发行版变化）。不使用命令行取屏工具。
- 同时打开本地开关并收到 Android 相机需求后，依次创建会话、按后端能力选择屏幕/窗口、显示系统授权、取得 PipeWire FD。每次重新启用都重新选择，不保存授权令牌；取消选择会在镜像错误区提示，可关闭/重开重试。
- 系统选择器未完成前不取图。取消授权会关闭请求与会话；系统停止共享、来源消失或传帧失败都会结束该次采集，不自动转向其它来源。优先用稳定的 PipeWire serial 定位；旧 Portal 未返回 serial 时使用选定 node ID。
- 原生 GStreamer 管线从 Portal FD 读取视频，转换为方形像素 BGRA 并等比缩放。流线程由 GStreamer 拥有，appsink 只保留最新帧；桌面 worker 串行读取和编码，停止时等待该帧结束，再销毁管线、关闭 FD 和会话。
- 取景是所选来源的完整画面，不跟随镜像窗口背后的区域，也不保证排除镜像本身。选择包含镜像的屏幕可能产生画面递归；可选择需要提供给手机的独立窗口。
- KWin 的区域取屏、隐藏调用方窗口属于桌面扩展，未接入此后端；标准 Portal 的窗口位置元数据也不提供可移植的镜像窗口全局坐标。

## 清理

- UI 拥有开关、需求、定时器、平台会话和 Windows 物理视口/affinity；worker 独占原生帧/JPEG 资源。off、禁用、媒体断开和关闭窗口都取消当前帧，等待 worker 退出后释放来源；窗口关闭需要时先取消关闭、清理后再关。
- 采集失败显示在镜像错误区，控制继续可用，可关闭/重开重试。

## 限制

- X11 和 Mac 没有采集后端；Wayland 各桌面的真实选择器、受保护画面、HDR、多显示器和锁屏行为取决于桌面实现。
- 创建 VirtualDevice 时包名与 UID 必须匹配：UID 0 的 `com.android.shell` 会失败，需使用 UID 2000 的 ADB Shizuku。
- 缺少 `GL_EXT_YUV_target` 的模拟器无法注册相机，需使用真机或支持该扩展的设备。
