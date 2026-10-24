# 开发约束

- UI 固定 Avalonia 12.1.4；ReactiveUI.Avalonia 12.1.6。
- 用 `[Reactive]` partial property 和 `[ReactiveCommand]` source generator。
- 协议、解码、发现、缓存、Windows/Linux 平台、桌面和各自测试保持独立 csproj。
- 保留 `src/Directory.Build.props` 的分析器，不批量屏蔽警告。
- 协议 DTO、请求/响应、帧、认证、地址规则和编解码归 `Andromeld.Protocol`。
- 桌面只调用类型化协议 API，不定义或解析协议消息。
- 协议响应用 typed record/DTO 反序列化，不用 `TryGetProperty` 链。
- 先确定线程所有者和调用约束，不靠不断增加 Lock 掩盖线程归属；详见 [ARCHITECTURE](docs/ARCHITECTURE.md)。
- 交互和协议以核对过的实际行为为准，区分行为证据、合成测试和真机结果。
- 普通应用 `task-state.away` 不得改成关闭窗口；以 `window-state.closed` 为关闭依据。
- 平台启动集中在 DesktopPlatform。
- 平台项目不依赖 Avalonia。
- Win32 P/Invoke 用 CsWin32，集中在 `Andromeld.Platform.Windows`；HWND 操作在创建线程执行。
- 每个窗口独立 AppUserModelID；应用镜像用实际应用图标。
- 剪贴板用 Avalonia `IClipboard`。
- Linux 文件拖出仅支持 KDE Wayland + KIO/Ark，不提供 `file://` 回退。
- 合成测试不能代替真机结论；`--live*` 会操作手机，默认使用模拟数据。
- 配对凭据、真实应用缓存、手机截图/视频、剪贴板正文和原始 WebRTC dump 不提交 Git。
- 测试按风险选择最小有效范围；外观、对齐、图标和尺寸变化只需构建与一次目视确认。

## 文档

- 一个事实只写一处：README=用法，ARCHITECTURE=边界/线程，子系统文档=实现细节；平台对照归 FEATURES，依赖版本以 `csproj` 为准。
- 功能分支只修改所属子系统文档；README/FEATURES/AGENTS/ARCHITECTURE 由集成分支统一更新。
- 不新增 HANDOFF/状态汇总；状态写 commit/PR。
- AGENTS 只放短规则，不放功能清单、教程、状态。

## Avalonia

- 集合用 `ObservableCollection<T>`；不在 ViewModel 创建控件或访问 `DataContext`；ViewModel 必须为 `partial`。
- 主题画刷用 `DynamicResource`；语义色和图标尺寸集中在 `Styles/ThemeResources.axaml`。
- 公共按钮样式在 `Styles/DesktopStyles.axaml`。
- 图标用 Lucide 矢量图标，不用 emoji；布局避免固定像素宽度、嵌套纵向滚动和散落的硬编码间距。
