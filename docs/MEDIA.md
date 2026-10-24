# 媒体解码

- 视频为 Annex B H.264，由 `Andromeld.Media` 的 FFmpeg worker 解码；worker 拥有解码器，解码帧合并后投递 UI。
- 仅在镜像收到视频配置时加载 FFmpeg：Linux 遵循 ldconfig、multiarch 目录和 `LD_LIBRARY_PATH`，直接将版本化 soname 交给系统动态加载器，不捆绑库；只有 `ffmpeg` 命令或旧版本共享库不能解码。其他平台保留程序目录和 `runtimes/<rid>/native/` 查找，`ANDROMELD_FFMPEG_LIBS` 可显式覆盖。初始化核对 avcodec/avutil/swscale ABI，失败只进入该镜像的错误状态，设备、文件和终端独立保留。
- 像素转换内存约束：swscale 输出到 av_malloc 对齐缓冲，行有 padding 且尾部保留 64 字节，再逐行复制成紧凑 BGRA；不要直接写紧凑托管缓冲，否则非对齐宽度会导致 native 崩溃。
- 渲染：手机镜像在 X11/Windows 用 SDL 纹理，Wayland/macOS 用 Avalonia 位图；应用镜像统一完整 Avalonia 位图，避免帧不同步。SDL 创建、纹理更新、渲染和销毁全在 UI，输入走 Avalonia 路由。窗口与装饰细节见 [MIRROR_WINDOWS](MIRROR_WINDOWS.md)。
