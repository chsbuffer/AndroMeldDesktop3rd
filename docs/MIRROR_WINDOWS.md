# 镜像窗口与显示

手机屏幕和应用镜像各自独立窗口。手机镜像保留系统标题栏，电源/最近任务/主页/返回在右下角，悬停提示显示实际快捷键。应用镜像使用 WindowDrawnDecorations；每个窗口独立 AppUserModelID，应用镜像用实际应用图标。Win32 窗口身份和 OLE 文件承诺有平台门控。

## 应用镜像标题栏

- 左侧应用图标/标题，右侧“返回 Esc”、三点菜单、最小化/全屏/关闭；全屏控制条含返回、显示菜单、最小化、退出全屏、关闭。
- “沉浸式顶栏”可开关：普通应用默认开、游戏默认关，按设备/应用/用户保存。开启时 edge-to-edge、按视频亮度切换主题并发送 decor-insets；关闭时标题栏在画面外、视频避开顶栏。
- 沉浸式覆盖高度从对称 WindowDecorationMargin 只扣外框/阴影；非沉浸和全屏发送 decor-insets=0。
- 全屏用 `WindowState.FullScreen`，顶部 1 DIP 触发 `FullscreenPopover`；最小化不覆盖普通窗口尺寸。
- 应用镜像按设备/应用/用户记住普通窗口尺寸和最大化状态；全屏和最小化不覆盖。
- `MirrorDisplayMenu` 的 `MenuFlyout` owner 必须是 `MirrorWindow`：装饰按钮不在 Window 视觉子树，直接作为 Popup owner 会 `IsOpen=true` 但不可见；用按钮屏幕位置换算 `PlacementRect`，不新增 Win32 调用。
- 标题配色在 UI 线程采样实际顶栏区域 32×4 个点；首次阈值 0.18，后续浅→深 0.155、深→浅 0.205，临界区保持当前配色。非沉浸模式取视频顶部主色作为不透明背景。

## 显示与生命周期

- `MainViewModel.Mirroring` 通过可等待的 MirrorRequested 创建窗口；窗口显示并完成 180ms 布局防抖后才创建虚拟显示；游戏也走同一路径，不先创建竖屏虚拟显示。
- `MirrorDisplayPreferences`、Settings 和 MirrorViewModel 的显示偏好由 UI 线程所有；按认证设备身份哈希保存；zoom、沉浸、相机和窗口尺寸按 `package:userId`，兼容按 package；修改后通知同设备窗口并经 180ms 防抖应用，`ConfigureDisplayAsync` 的调用者必须串行。
- 三点菜单提供 50%–150% 缩放（步长 10%）和兼容分辨率；缩放改变 Android 显示密度，兼容模式按 392 DIP 短边计算虚拟尺寸。仅沉浸式顶栏按有效 DPR 发送 decor-insets，非沉浸和全屏为 0。协议细节见 [PROTOCOL](PROTOCOL.md)。
- “截图”按原始视频分辨率保存 PNG，不含标题栏、菜单、窗口黑边或背后桌面。
- 应用镜像固定 `UseNativeRendering=false`，整帧用一个 Avalonia Image/WriteableBitmap 绘制，与透明装饰同次合成；手机镜像在 X11/Windows 使用 SDL 子窗口，holder 和 SDL 两层 HWND 都必须对 `WM_NCHITTEST` 返回 `HTTRANSPARENT`。
- Linux（KWin/Wayland）应用镜像先请求部分装饰，再恢复完整装饰以进入自绘模式；手机和其他窗口不执行此切换。自绘主题负责沉浸、取色、按钮和全屏浮条，不全局强制客户端装饰。
- 普通应用 `task-state.away` 显示“重新启动”，只有 `window-state.closed` 关闭窗口；详见 [PROTOCOL](PROTOCOL.md)。手机镜像忽略应用 away。
- 输入、Esc 和快捷键见 [MIRROR_INPUT](MIRROR_INPUT.md)；融合模式与关屏使用见 [PHONE_MIRROR_MODES](PHONE_MIRROR_MODES.md)。
