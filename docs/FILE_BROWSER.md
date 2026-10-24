# 文件浏览

文件浏览使用独立窗口和 window-scoped `FileBrowserViewModel`；`MainViewModel` 只保留连接 attach/detach 和窗口入口。窗口关闭只取消文件操作，不释放共享连接；关闭 `Suspend`、重开 `Resume`、断开 `Detach`。

## 导航与视图

- 存储根、路径、前进/后退历史、树缓存、下载/上传/删除和拖出批次归 `FileBrowserViewModel`，全部在 UI 线程。详情、图标和树共享完整路径行模型。
- 详情/图标/树三个视图用三个 RadioButton 图标 toggle，不用 ComboBox；面包屑以存储根为边界，Ctrl+L/F4 编辑完整路径。
- 后退/前进历史只在当前存储根内维护；地址栏“上一级”右侧有按钮，鼠标 XButton1/XButton2 也可导航。刷新不重复入栈，后退后访问新目录会截断前进历史，切换存储根或断开时清空。
- 树按需加载子目录；导航/关闭取消未完成的展开，折叠保留缓存、刷新重载。
- 文件图标按扩展名优先、MIME 兜底映射 Lucide 类型图标。图标视图用 `AdaptiveTilePanel` 保留方向键跨列导航。

## 选择与操作

- `ListBox` 绑定 typed `SelectionModel<FileBrowserItem>`（`SingleSelect=false`），支持 Ctrl/Shift 和框选；空白处拖动绘制选框，已多选行上的普通按下延迟到释放再收敛，保证拖动不会先清空选择。
- 详情/图标/树使用统一选中背景和边框。
- 详情表头按名称、大小或修改时间排序，再次点击切换升序/降序，文件夹始终优先；排序递归应用于已加载的树分支，排序后保留选中项。右键菜单也提供排序、刷新和全选。
- 快捷键：F5 刷新、Ctrl+A 全选、Backspace 上一级、Alt+←/→ 后退/前进、Delete 永久删除（二次确认）。
- 右键菜单提供多选下载、永久删除和复制路径。下载和拖出按选中行路径操作，不用当前目录重新拼接树后代。
- 系统剪贴板没有远程文件承诺，不做手机→系统复制或系统→手机粘贴；只提供多选下载、删除、复制路径。多选下载复用 `FileDragBatch.CopyToDirectoryAsync` 的原子 partial、同名编号和清理逻辑。删除走 `fs-delete`/`fs-op-result` 类型化 DTO，批次最多 64 路径，二次确认后刷新目录。
- 文件接收、上传和拖出见 [FILE_DRAG](FILE_DRAG.md)；文件传输协议见 [PROTOCOL](PROTOCOL.md)。
