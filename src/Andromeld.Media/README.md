# 本地构建 FFmpeg

源码：https://github.com/FFmpeg/FFmpeg/archive/refs/tags/n9.0.1.zip

## Linux / Ubuntu 22.04 x86_64

在仓库根目录运行。sysroot 创建要求系统已安装 `sudo`、`debootstrap` 和 `ubuntu-keyring`；FFmpeg 编译需要 Bash、Python 3、Clang、LLD、LLVM 工具、GNU Make 和 NASM（最低版本由构建脚本检查）。

```sh
mkdir -p artifacts/sysroots
sudo bash build/linux-sysroot/build-linux-sysroot.sh artifacts/sysroots/ubuntu-22.04-x86_64
bash tools/build-ffmpeg-linux.sh /path/to/FFmpeg-n9.0.1.tar.gz
```

sysroot 脚用 debootstrap 和 Ubuntu archive keyring 建立 jammy rootfs，再在 chroot 内安装发行版自带的构建工具和开发库、用 `symlinks -cr /usr` 转换绝对链接。目标目录必须尚不存在；`UBUNTU_MIRROR`、`UBUNTU_CODENAME`、`UBUNTU_COMPONENTS` 和 `UBUNTU_EXTRA_PACKAGES` 可覆盖默认输入。

FFmpeg 脚本使用宿主 Clang/LLD，显式设置 `--target=x86_64-linux-gnu`、`--sysroot` 和 sysroot 内的 GCC 工具链目录；关闭外部依赖自动探测，保留 H.264 解码、swscale、线程和运行时 SIMD 分派。可通过 `NASM=/path/to/nasm` 指定宿主汇编器，`JOBS=` 指定并行数，第二、第三个位置参数分别指定 sysroot 和安装目录。

默认安装到 `artifacts/ffmpeg/9.0.1/ubuntu-22.04-x86_64/`，共享库在 `lib/`，同时保留开发头、许可证、源码包校验和及构建配置。中间源码和编译目录在 `artifacts/ffmpeg-build/`；脚本会检查产物为 x86_64 且 glibc 符号要求不高于目标基线。

使用本地库运行合成媒体检查：

```sh
ffmpeg_libs="$PWD/artifacts/ffmpeg/9.0.1/ubuntu-22.04-x86_64/lib"
LD_LIBRARY_PATH="$ffmpeg_libs${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" \
  ANDROMELD_FFMPEG_LIBS="$ffmpeg_libs" \
  dotnet run --project tests/Andromeld.Media.Tests
```

`LD_LIBRARY_PATH` 也用于解析 FFmpeg 库之间的依赖。桌面运行时的库查找规则见 [MEDIA](../../docs/MEDIA.md)。生成的 sysroot 和二进制均留在 Git 忽略的 `artifacts/` 中。

## Windows / MSYS2 UCRT

```sh
pacboy -S toolchain nasm
export LDFLAGS="-static-libgcc -static-libstdc++ -static"

./configure --disable-everything --disable-static --disable-doc --disable-programs \
  --enable-avcodec --enable-avutil --enable-swscale --enable-shared \
  --enable-decoder=h264 --enable-lto --enable-stripping \
  --disable-avformat --disable-swresample --disable-avdevice --disable-avfilter \
  --disable-debug --prefix=/c/a/FFmpeg-n9.0.1/build
make -j8
make install
```

构建桌面应用前，仅将 `bin/avcodec-63.dll`、`bin/avutil-61.dll`、`bin/swscale-10.dll` 放入 `artifacts/ffmpeg/9.0.1/runtimes/win-x64/native/`；许可证可放 `artifacts/ffmpeg/9.0.1/licenses/ffmpeg/`。这些文件由用户准备，不自动下载、不纳入版本控制。未指定 RID 时保留 `runtimes/<rid>/native/`，指定 RID 时平铺到程序目录。详见 [本地 native assets](../../docs/NATIVE_ASSETS.md)。
