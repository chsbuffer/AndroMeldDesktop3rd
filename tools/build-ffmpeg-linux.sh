#!/usr/bin/env bash
set -euo pipefail

if [[ ${1:-} == --help || $# -lt 1 || $# -gt 3 ]]; then
    echo "Usage: $0 <FFmpeg-n9.0.1.zip|FFmpeg-n9.0.1.tar.gz> [ubuntu-22.04-sysroot] [install-directory]"
    [[ ${1:-} == --help ]] && exit 0
    exit 2
fi

repo_root=$(cd -- "$(dirname -- "$0")/.." && pwd)
[[ $(uname -s)-$(uname -m) == Linux-x86_64 ]] || { echo "Requires a Linux x86_64 host." >&2; exit 1; }
archive=$(realpath -- "$1")
sysroot=$(realpath -m -- "${2:-$repo_root/artifacts/sysroots/ubuntu-22.04-x86_64}")
prefix=$(realpath -m -- "${3:-$repo_root/artifacts/ffmpeg/9.0.1/ubuntu-22.04-x86_64}")
work_dir="$repo_root/artifacts/ffmpeg-build/9.0.1"

for tool in clang ld.lld llvm-ar llvm-nm llvm-ranlib llvm-strip make tar unzip readelf python3; do
    command -v "$tool" >/dev/null || { echo "Required command not found: $tool" >&2; exit 1; }
done
[[ -f $archive ]] || { echo "Source archive not found: $archive" >&2; exit 1; }
[[ -f $sysroot/usr/include/stdio.h && -d $sysroot/usr/lib/gcc/x86_64-linux-gnu/11 ]] || {
    echo "Create the Ubuntu 22.04 sysroot first with build/linux-sysroot/build-linux-sysroot.sh." >&2
    exit 1
}

mkdir -p -- "$work_dir"
build_dir=$(mktemp -d "$work_dir/ubuntu-22.04-XXXXXX")
# Keep the build directory and config.log for inspection or rebuilding.
echo "Build directory: $build_dir"
source_dir="$build_dir/source"
case "$archive" in
    *.zip)
        unzip -q "$archive" -d "$build_dir"
        mv -- "$build_dir/FFmpeg-n9.0.1" "$source_dir"
        ;;
    *.tar.gz|*.tgz)
        mkdir -p -- "$source_dir"
        tar -xzf "$archive" --strip-components=1 -C "$source_dir"
        ;;
    *) echo "Expected a .zip or .tar.gz source archive." >&2; exit 1 ;;
esac
[[ -f $source_dir/RELEASE && $(< "$source_dir/RELEASE") == 9.0.1 ]] || {
    echo "Expected an FFmpeg 9.0.1 source archive." >&2; exit 1;
}
chmod +x "$source_dir/configure"

export ANDROMELD_FFMPEG_SYSROOT="$sysroot"
cat > "$build_dir/target-cc" <<'EOF'
#!/bin/sh
exec clang --target=x86_64-linux-gnu --sysroot="$ANDROMELD_FFMPEG_SYSROOT" \
    --gcc-toolchain="$ANDROMELD_FFMPEG_SYSROOT/usr" -fuse-ld=lld "$@"
EOF
chmod +x "$build_dir/target-cc"

assembler=${NASM:-$(command -v nasm || true)}
[[ -x $assembler ]] || { echo "NASM is required." >&2; exit 1; }
python3 - "$assembler" <<'PY'
import re
import subprocess
import sys

version = subprocess.check_output([sys.argv[1], '-v'], text=True)
match = re.search(r'version (\d+)\.(\d+)', version)
if not match or tuple(map(int, match.groups())) < (3, 1):
    raise SystemExit('NASM 3.01 or newer is required for this FFmpeg build.')
PY

cd -- "$build_dir"
"$source_dir/configure" \
    --prefix="$prefix" --target-os=linux --arch=x86_64 --cpu=x86-64 \
    --enable-cross-compile --sysroot="$sysroot" \
    --cc="$build_dir/target-cc" --ld="$build_dir/target-cc" --host-cc=clang \
    --ar=llvm-ar --nm=llvm-nm --ranlib=llvm-ranlib --strip=llvm-strip \
    --x86asmexe="$assembler" --extra-cflags='-march=x86-64 -mtune=generic' \
    --disable-autodetect --disable-everything --disable-static --disable-doc --disable-programs \
    --enable-avcodec --enable-avutil --enable-swscale --enable-shared --enable-pic \
    --enable-decoder=h264 --enable-lto --enable-stripping \
    --disable-avformat --disable-swresample --disable-avdevice --disable-avfilter \
    --disable-network --disable-debug
make -j "${JOBS:-$(getconf _NPROCESSORS_ONLN)}"
make install

mkdir -p -- "$prefix/share/licenses/ffmpeg"
cp -- "$source_dir/COPYING.LGPLv2.1" "$source_dir/LICENSE.md" "$prefix/share/licenses/ffmpeg/"
cp -- config.h ffbuild/config.mak "$prefix/share/licenses/ffmpeg/"
sha256sum "$archive" > "$prefix/share/licenses/ffmpeg/source.sha256"

python3 - "$prefix/lib" <<'PY'
from pathlib import Path
import re
import subprocess
import sys

for name, major in (("avcodec", 63), ("avutil", 61), ("swscale", 10)):
    library = Path(sys.argv[1]) / f"lib{name}.so.{major}"
    info = subprocess.check_output(["readelf", "--version-info", "--wide", str(library)], text=True)
    versions = {tuple(map(int, match.split('.'))) for match in re.findall(r"Name: GLIBC_([0-9.]+)", info)}
    if not versions or max(versions) > (2, 35):
        raise SystemExit(f"Unexpected glibc requirements for {library}: {sorted(versions)}")
    header = subprocess.check_output(["readelf", "--file-header", str(library)], text=True)
    if "Advanced Micro Devices X86-64" not in header:
        raise SystemExit(f"Unexpected target architecture: {library}")
    print(f"Verified {library.name}: x86_64, maximum required GLIBC_{'.'.join(map(str, max(versions)))}")
PY
echo "FFmpeg libraries: $prefix/lib"
