# 镜像输入

## 键盘与输入法

- 手机和应用镜像都支持 Esc 返回；菜单打开时 Esc 先关菜单。快捷键默认 Ctrl+R 最近任务、Ctrl+H 主页；设置支持 Ctrl/Alt/Shift 的 7 种非空组合，悬停提示显示实际快捷键。修饰键只用于本地匹配，不附加到 Android 导航键。
- 手机和应用镜像共用 `VideoView` 输入法客户端；点击视频后启用系统 IME，候选窗定位在最近点击处（协议未提供远端输入框光标）。
- 拼音等未提交的组合文本仅显示在本地无焦点 Popup 中，独立表面避免被手机镜像的 SDL 子窗口盖住。提交后通过现有类型化 `TextAsync` 发送 Unicode 文本，不转发组合文本。
- 组合期间不向 Android 转发回车、退格、方向键、Esc 或镜像快捷键；组合结束后恢复原有键盘操作。失焦、窗口停用和关闭清理组合状态。
- 不提供虚构的远端 surrounding text 或编辑选区；本地组合与候选由系统输入法管理。
- 框架依据：[Avalonia 12.1.4 输入法管理器](https://github.com/AvaloniaUI/Avalonia/blob/2d79ba55fae1b1e71c69f93a668080c9029e6a31/src/Avalonia.Base/Input/TextInput/InputMethodManager.cs) 在焦点变化时查询 `TextInputMethodClientRequested`；没有客户端便禁用输入法交互。
- 终端内 Esc 归 Shell，不触发 Android 返回。窗口标题栏和全屏控制见 [MIRROR_WINDOWS](MIRROR_WINDOWS.md)。

## 鼠标

- 未按键划过真实视频区域时发 `pointer/hover`；触摸、画面外和黑边不合成 hover；拖动仍发 down/move/up。
- 右键默认模拟 Android 长按：按下立即发 down，松开不足 600ms 就补足再 up；按住期间可移动，快速松开后保留位置。菜单由远端应用响应长按显示，没有桌面菜单协议。
- 设置 → 镜像控制 → “右键返回”默认关闭；开启后右键发 Back，不再发送长按。设置自动保存，手机/应用镜像共享并即时生效。
- 捕获丢失、失焦或关闭会 cancel 当前按压并停止延迟计时器；正常捕获释放不会提前取消长按。状态全在 UI 线程，无锁、无后台输入轮询。
- 依据：Web 客户端对非触摸的 pointermove 发送 hover；右键 up 不足 600ms 时补足，并拦截 `contextmenu`；Android 客户端接受 hover 并转发给 helper。桌面没有浏览器 contextmenu 回退，也未增加 Ctrl+左键手势。
