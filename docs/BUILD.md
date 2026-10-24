# 构建、发布与本地依赖

## 构建与运行

```sh
dotnet build Andromeld.slnx
dotnet run --project src/Andromeld.Desktop
```

SDK 版本见 `global.json`；NuGet 依赖及精确版本以各 `*.csproj` 为准，Avalonia/ReactiveUI 版本由 [AGENTS](../AGENTS.md) 固定。

[`Build Desktop`](../.github/workflows/build-desktop.yml) 在分支和 tag push 上运行。Windows 和 Linux 两个独立 job 均先恢复或编译 FFmpeg，再使用 `global.json` 指定的 SDK 发布本体；只有 tag push 发布 GitHub Release。

## ADB

`build/Adb.targets` 下载 Google platform-tools **37.0.1** 到 `artifacts/adb/37.0.1/`，首次需联网。未指定 RID 时携带 win-x64/linux-x64 的 `runtimes/<rid>/native/`；指定 RID 时平铺到输出根目录。ADB 不嵌入单文件程序。

## FFmpeg

需要 FFmpeg 9 共享库（libavcodec 63、libavutil 61、libswscale 10）。`dotnet build/publish` 的 Linux 输出不包含 FFmpeg，也不自动下载；AppImage 可按下文加入本地库。其他平台构建前放到 `artifacts/ffmpeg/9.0.1/runtimes/<rid>/native/`，Windows x64 只需 `avcodec-63.dll`、`avutil-61.dll`、`swscale-10.dll`。`-p:FFmpegCacheDirectory=` 可改构建输入目录。运行时查找、ABI 核对和失败行为见 [MEDIA](MEDIA.md)。

CI 从 [FFmpeg n9.0.1 ZIP 源码](https://github.com/FFmpeg/FFmpeg/archive/refs/tags/n9.0.1.zip) 编译，仅启用 H.264 解码及 avcodec / avutil / swscale 共享库。Windows 使用 MSYS2 UCRT64；Linux 使用 Clang / LLD 和仓库现有脚本生成的 Ubuntu 22.04 sysroot 交叉编译，并检查 x86_64 架构及最高 glibc 2.35 要求。sysroot 缓存为 tar.xz，排除 dev / proc / sys / run / tmp / var/tmp，编译前解压。

FFmpeg 构建结果通过 `actions/cache` 按平台缓存整个 `artifacts/ffmpeg/9.0.1/`，构建脚本变化时失效；Linux 的 key 还包含 sysroot 脚本。命中时跳过源码下载和编译，Linux 同时跳过 sysroot 准备，Windows 跳过 MSYS2 安装。Windows 库位于 `runtimes/win-x64/native/`，Linux 库位于供 AppImage 使用的 `linux-x64/native/`（保留共享库符号链接）；两者均包含 `licenses/ffmpeg/`、构建配置、源码地址与 SHA-256。保留分支构建以填充默认分支缓存，供后续 tag 构建复用；[GitHub 缓存作用域](https://docs.github.com/en/actions/reference/workflows-and-actions/dependency-caching#restrictions-for-accessing-a-cache)不允许不同 tag 直接复用彼此的缓存。

本地编译可使用 `tools/build-ffmpeg-windows.sh <FFmpeg-n9.0.1.zip> [安装目录]`（MSYS2 UCRT64）或 `tools/build-ffmpeg-linux.sh <源码 ZIP 或 tar.gz> [sysroot] [安装目录]`（Linux x86_64，NASM ≥ 3.01）。

## 发布

```sh
dotnet publish src/Andromeld.Desktop -c Release -r win-x64 -o artifacts/publish/win-x64
dotnet publish src/Andromeld.Desktop -c Release -r linux-x64 -o artifacts/publish/linux-x64
./artifacts/publish/linux-x64/Andromeld.Desktop
```

配置为自包含单文件、`TrimMode=full`、ReadyToRun；`Andromeld.Protocol`、EF Core/SQLite 和终端 XAML 作为裁剪根，`Porta.Pty` 的 ConPTY 被移除。full trim 缺根会发送空认证 JSON、破坏 EF Core 比较器或终端 XAML；保留运行时 COM 封送和 JSON 反射。原生 DLL 与 FFmpeg 许可证独立放置。

CI 的 `AndroMeld-win-x64` artifact 包含完整 Windows 发布目录的 ZIP；`AndroMeld-linux-x64` artifact 包含 AppImage 的 tar.xz，以保留可执行权限。两者均携带源码编译的 FFmpeg 和许可证；发布包统一收集到 `artifacts/release/`。

自行创建并推送 tag（如 `git tag v1.0.0`、`git push origin v1.0.0`）后，两个平台构建成功即发布同名 GitHub Release，并附上发布目录中的两平台包及自动生成的发布说明。重跑时更新已有 Release 的附件。提交消息不影响发布，也不自动生成 tag。

### Linux AppImage

在 Linux x86_64 上安装仓库要求的 .NET SDK、`curl` 和 `desktop-file-validate`（通常由 `desktop-file-utils` 提供），然后运行：

```sh
sh tools/build-appimage.sh
# 可选：指定输出目录，相对路径以调用时的当前目录为准
sh tools/build-appimage.sh /path/to/output
```

默认生成 `artifacts/appimage/AndroMeld-x86_64.AppImage`。脚本复用 Release / linux-x64 发布配置，将完整发布目录、ADB 和现有应用图标装入 AppDir；构建临时目录在退出时清理，成功后才替换最终产物。目前仅支持 x64，与仓库内置的 Linux ADB 目标一致。

脚本首次从 [appimagetool 官方发行页](https://github.com/AppImage/appimagetool/releases/tag/1.9.1) 下载工具并缓存到 `artifacts/tools/`；NuGet、ADB 和 AppImage runtime 下载也需要网络。打包工具以 extract-and-run 模式运行，构建无需 FUSE；可用 appimagetool 的 `APPIMAGETOOL_RUNTIME_FILE` 环境变量指定本地 runtime。

```sh
./artifacts/appimage/AndroMeld-x86_64.AppImage
# 无法使用 FUSE 时
./artifacts/appimage/AndroMeld-x86_64.AppImage --appimage-extract-and-run
```

AppImage 自带 .NET 运行时。打包前可将本地编译的 `libavcodec.so*`、`libavutil.so*`、`libswscale.so*`（保留符号链接）放到 `artifacts/ffmpeg/9.0.1/linux-x64/native/`，许可证放到同版本目录的 `licenses/ffmpeg/`；脚本会一并打包，启动器通过 `LD_LIBRARY_PATH` 优先加载随包库。未提供该目录时使用系统 FFmpeg。系统图形库及屏幕摄像头所需的 portal / PipeWire / GStreamer 由运行环境提供。此脚本不保证跨发行版兼容；应在计划支持的最老发行版上打包，并在目标环境测试。

## 版本信息

版本信息为 12 位 commit hash + UTC 构建时间；无 Git 时可用 `-p:GitCommitHash=... -p:GitCommitTimestamp=...`。

本地 native assets 接入见 [NATIVE_ASSETS](NATIVE_ASSETS.md)。
