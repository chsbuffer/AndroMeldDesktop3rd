#!/usr/bin/env bash
set -euo pipefail

if [[ ${1:-} == --help || $# -lt 1 || $# -gt 2 ]]; then
    echo "Usage: $0 <FFmpeg-n9.0.1.zip> [install-directory]"
    [[ ${1:-} == --help ]] && exit 0
    exit 2
fi

[[ ${MSYSTEM:-} == UCRT64 && $(uname -m) == x86_64 ]] || {
    echo "Requires an MSYS2 UCRT64 x86_64 shell." >&2; exit 1;
}
repo_root=$(cd -- "$(dirname -- "$0")/.." && pwd)
archive=$(realpath -- "$1")
prefix=$(realpath -m -- "${2:-$repo_root/artifacts/ffmpeg/9.0.1/windows-x86_64}")
work_dir="$repo_root/artifacts/ffmpeg-build/9.0.1"

for tool in gcc gcc-ar gcc-nm gcc-ranlib strip nasm make unzip; do
    command -v "$tool" >/dev/null || { echo "Required command not found: $tool" >&2; exit 1; }
done
[[ -f $archive ]] || { echo "Source archive not found: $archive" >&2; exit 1; }

mkdir -p -- "$work_dir"
build_dir=$(mktemp -d "$work_dir/win-x64-XXXXXX")
# Keep the build directory and config.log for inspection or rebuilding.
echo "Build directory: $build_dir"
unzip -q "$archive" -d "$build_dir"
source_dir="$build_dir/FFmpeg-n9.0.1"
[[ -f $source_dir/RELEASE && $(< "$source_dir/RELEASE") == 9.0.1 ]] || {
    echo "Expected an FFmpeg 9.0.1 source archive." >&2; exit 1;
}
chmod +x "$source_dir/configure"

cd -- "$build_dir"
"$source_dir/configure" \
    --prefix="$prefix" --target-os=mingw32 --arch=x86_64 --cpu=x86-64 \
    --cc=gcc --ar=gcc-ar --nm=gcc-nm --ranlib=gcc-ranlib --strip=strip \
    --extra-cflags='-march=x86-64 -mtune=generic' --extra-ldflags='-static -static-libgcc' \
    --disable-autodetect --disable-everything --disable-static --disable-doc --disable-programs \
    --enable-avcodec --enable-avutil --enable-swscale --enable-shared \
    --enable-decoder=h264 --enable-lto --enable-stripping \
    --disable-avformat --disable-swresample --disable-avdevice --disable-avfilter \
    --disable-pthreads --enable-w32threads --disable-network --disable-debug
make -j "${JOBS:-$(nproc)}"
make install

mkdir -p -- "$prefix/share/licenses/ffmpeg"
cp -- "$source_dir/COPYING.LGPLv2.1" "$source_dir/LICENSE.md" "$prefix/share/licenses/ffmpeg/"
cp -- config.h ffbuild/config.mak "$prefix/share/licenses/ffmpeg/"
sha256sum "$archive" > "$prefix/share/licenses/ffmpeg/source.sha256"

for library in avcodec-63.dll avutil-61.dll swscale-10.dll; do
    [[ -f $prefix/bin/$library ]] || { echo "Required library not found: $library" >&2; exit 1; }
done
echo "FFmpeg libraries: $prefix/bin"
