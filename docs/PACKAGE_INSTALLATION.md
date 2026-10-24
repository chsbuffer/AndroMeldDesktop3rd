# Android 安装包

## 边界与生命周期

- `Andromeld.Packages` 独立处理本地 APK/ZIP、AXML、ARSC、签名证书和安装文件选择，不依赖 Avalonia、ADB 或网络协议。
- `PackageArchive.OpenAsync` 在 worker 上创建临时快照并解包；解出的文件使用内部编号，不使用 ZIP 路径作为本地路径。确认后上传同一份快照，原始文件改变不会替换用户已查看的内容。快照由安装窗口持有，关闭时先取消并等待活动操作，再删除文件。
- `PackageInstallerViewModel` 的属性、集合、命令与 bitmap 属于 UI 线程。主窗口协调安装窗口；安装窗口绑定主窗口当前连接，未连接时可先在主窗口完成连接。只有确认安装才查询设备、上传及执行安装。
- 首次启动读取第一个文件参数。`DesktopInstance` 使用 BCL 命名互斥量确定当前用户的进程归属，显式设置 `CurrentUserOnly = true` 和 `CurrentSessionOnly = false`，在不同终端会话间保持同一个实例；同用户命名管道传递长度限定的 UTF-8 路径，并等待接收确认。发送进程先把相对路径转为绝对路径；监听器只写请求队列，UI 线程在启动提示结束后消费。相同路径激活已有安装窗口，不同路径各自保留确认窗口，继续使用主进程的连接。
- `Andromeld.Protocol` 提供类型化兼容性查询、安装输入和进度；桌面不拼接 shell 命令或解析终端输出。每次安装独占一个临时 PTY，读取和 ACK 与输入并行，避免大批分包的终端回显阻塞输入。

## 包与元数据

APK 直接解析；APKS、APKM、XAPK 按 ZIP 读取其中的 APK。容器内 APK 必须具有相同的包名与版本，并包含基础包。加密容器不能作为普通 ZIP 安装。单个输入、解压总量及一次传输总量上限为 4 GiB；容器最多 512 个 APK/OBB，安装接口最多 64 个 OBB。

有 `toc.pb` 的 APKS 按 bundletool 目录选择一个 SDK/ABI/屏幕密度变体，并按设备 ABI 选择原生分包；同一变体的语言和 density 资源分包全部安装，资源由 Android 在运行时选择。目录中的 on-demand/instant 模块不随基础应用安装。Play Asset Delivery、额外设备/地区/纹理定向等未实现的分发形式明确报错，要求提供针对设备导出的包。

没有 `toc.pb` 的容器按 manifest 区分基础包与 split，优先 universal，再按最低 SDK 和设备 ABI 选基础变体；保留同组资源 split，过滤不匹配的 native split。同名 split 变体和没有匹配 native ABI 的包会报错。

AXML 读取包名、版本、权限和 SDK；ARSC 解引用应用名及图标，支持 UTF-8/UTF-16 字符串、普通/sparse/offset16/compact 条目。图标可从 density split 的资源及文件补齐；常见位图和 vector/adaptive 图标在桌面渲染，无可用图标时使用 Lucide 包图标。

证书读取 JAR v1 的 PKCS#7 证书集合，以及 v2/v3/v3.1 signing block 的 signer certificate。显示 subject 和 DER 证书的 SHA-256；这里只提取身份信息，**不声称验证 APK 签名有效**，最终校验由 Android 执行。

格式依据：[Android APK signing block](https://source.android.com/docs/security/features/apksigning/v2)、[bundletool commands.proto](https://github.com/google/bundletool/blob/master/src/main/proto/commands.proto)、[targeting.proto](https://github.com/google/bundletool/blob/master/src/main/proto/targeting.proto)；AXML/ARSC 布局参考 [Android ResourceTypes](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/libs/androidfw/ResourceTypes.h)。

## 传输与安装

复用已核对的 Web `fs`、文件上传和 `terminal` 能力，不新增未确认的安装消息。APK 中的 `agent:install-apk` 是 agent 管理入口，只有单 APK 与 64 MiB 限额，不作为通用分包安装接口。

1. 通过终端读取设备 SDK、支持的 ABI 和 `wm density`，选择兼容文件。
2. 在 `fs-roots` 返回的存储根下创建唯一的 `.andromeld-install-*` 暂存目录，按编号上传 APK/OBB。
3. 通过终端运行 `pm install-create -r -S`，逐个 `cat file | pm install-write -S … -`，最后 `pm install-commit`。APK 字节通过管道传入，避免 `cmd` 经 Binder 将外部存储文件描述符交给无权读取它的系统服务；直接 `< file` 重定向仍会传递该文件描述符。会话失败时用 shell trap 执行 `install-abandon`。脚本使用分行 base64 heredoc，避免 PTY 的单行输入长度限制；随机起止标记隔离终端回显和执行结果。
4. APK 提交成功后，才将 XAPK 的 OBB 复制到相同存储根的 `Android/obb/<package>/`，防止签名冲突等失败更新覆盖已有应用的 OBB。OBB 路径限定为该包的直接文件。复制失败明确显示“应用已安装，但 OBB 扩展数据复制失败”，允许重试。
5. 关闭 PTY 并尝试删除暂存目录。断连可能留下暂存目录；取消/断连提示用户检查手机状态，不推断 Android 已回滚或安装成功。

系统命令依据：[AOSP PackageManagerShellCommand](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/services/core/java/com/android/server/pm/PackageManagerShellCommand.java)、[cmd 的 Binder 标准流传递](https://android.googlesource.com/platform/frameworks/native/+/refs/heads/main/cmds/cmd/cmd.cpp)。具体 ROM 对 shell、外部存储和 OBB 的权限限制仍可能令安装失败，系统输出在窗口中显示；需要在手机上确认时遵循系统流程。
