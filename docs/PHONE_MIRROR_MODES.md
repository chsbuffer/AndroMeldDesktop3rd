# 手机镜像：融合模式与关屏使用

- `[4,1] {c:"focused-app",pkg,userId}` 是会话内前台应用。无包名时禁用融合；点击时从当前已认证设备的可启动列表精确匹配 package/userId，找不到提示先在手机打开，不跨 Android 用户回退。融合只做一次打开/聚焦，不保存开关，不发专用 fusion 消息。
- 融合复用既有开窗路径：匹配已有窗口的初始应用或当前 focused-app 时激活，否则按实际视口/DPI 创建独立应用镜像。手机窗口保持打开，不主动关屏；普通应用 away 仍保留窗口，只有 closed 关闭。
- 关屏：当前手机 `/media` 发 `[4,1] {c:"display-power",on:false}`，on:true 点亮。按钮先更新选中状态，不等待不存在的逐请求 ACK，使用输入通道的串行 writer。普通应用的类型化 API 忽略该操作；状态不写 Settings，也不用 Android Power 键替代。
- attach 可重放 focused-app、display-power-state 和保持关屏时的 display-power(false)。同名 display-power 通知更新本地状态；display-power-state 仅在 state=1 且 reason=4 时取消关屏选中。APK 清理 phone 会话时恢复显示输出；客户端沿用 close-window 和媒体释放，不额外发 on:true。
- Protocol 定义 FocusedApplication、DisplayPowerState 和媒体解析；reader 只发类型化事件，MirrorViewModel 投递 UI，UI 独占前台目标、关屏状态和命令。MainViewModel 协调应用窗口，不新增线程/锁。
