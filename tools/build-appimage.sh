#!/bin/sh
set -eu

usage() {
    printf 'Usage: %s [output-directory]\n' "$0"
    printf 'Build a Linux x64 AppImage (default: artifacts/appimage/).\n'
}

case "${1:-}" in
    -h|--help) usage; exit 0 ;;
esac
if [ "$#" -gt 1 ]; then
    usage >&2
    exit 1
fi

if [ "$(uname -s)-$(uname -m)" != Linux-x86_64 ]; then
    printf 'AppImage packaging currently requires Linux x86_64 (the bundled ADB target is linux-x64).\n' >&2
    exit 1
fi

for command in dotnet curl desktop-file-validate; do
    if ! command -v "$command" >/dev/null 2>&1; then
        printf 'Required command not found: %s\n' "$command" >&2
        exit 1
    fi
done

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
output_dir=${1:-"$repo_root/artifacts/appimage"}
mkdir -p -- "$output_dir"
output_dir=$(CDPATH= cd -- "$output_dir" && pwd)
build_dir=$(mktemp -d "$output_dir/.build-XXXXXX")
trap 'rm -rf -- "$build_dir"' EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

appimagetool_version=1.9.1
appimagetool="$repo_root/artifacts/tools/appimagetool-$appimagetool_version-x86_64.AppImage"
if [ ! -x "$appimagetool" ]; then
    mkdir -p -- "$(dirname -- "$appimagetool")"
    curl --fail --location --show-error --retry 3 \
        "https://github.com/AppImage/appimagetool/releases/download/$appimagetool_version/appimagetool-x86_64.AppImage" \
        --output "$build_dir/appimagetool.AppImage"
    chmod +x "$build_dir/appimagetool.AppImage"
    mv -- "$build_dir/appimagetool.AppImage" "$appimagetool"
fi

app_dir="$build_dir/AndroMeld.AppDir"
mkdir -p -- "$app_dir/usr/bin"
cd -- "$repo_root"
dotnet publish src/Andromeld.Desktop/Andromeld.Desktop.csproj \
    --configuration Release --runtime linux-x64 --self-contained true \
    --output "$app_dir/usr/bin"

# Supplying this directory opts into bundling locally built Linux FFmpeg.
ffmpeg_cache="$repo_root/artifacts/ffmpeg/9.0.1"
ffmpeg_native="$ffmpeg_cache/linux-x64/native"
if [ -d "$ffmpeg_native" ]; then
    for library in libavcodec.so.63 libavutil.so.61 libswscale.so.10; do
        if [ ! -f "$ffmpeg_native/$library" ]; then
            printf 'Required FFmpeg library not found: %s/%s\n' "$ffmpeg_native" "$library" >&2
            exit 1
        fi
    done
    cp -P -- "$ffmpeg_native"/*.so* "$app_dir/usr/bin/"
    mkdir -p -- "$app_dir/usr/bin/licenses/ffmpeg"
    cp -- "$ffmpeg_cache"/licenses/ffmpeg/* "$app_dir/usr/bin/licenses/ffmpeg/"
fi

# MSBuild/native asset copies must retain executable permissions in the image.
chmod +x "$app_dir/usr/bin/Andromeld.Desktop" "$app_dir/usr/bin/adb"
cp -- "$repo_root/src/Andromeld.Desktop/Assets/app_icon.png" "$app_dir/andromeld.png"
ln -s andromeld.png "$app_dir/.DirIcon"

cat > "$app_dir/andromeld.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=AndroMeld
Comment=Connect to your Android device
Exec=Andromeld.Desktop %f
Icon=andromeld
Terminal=false
Categories=Utility;
EOF

cat > "$app_dir/AppRun" <<'EOF'
#!/bin/sh
set -eu
app_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
if [ -f "$app_dir/usr/bin/libavcodec.so.63" ]; then
    export LD_LIBRARY_PATH="$app_dir/usr/bin${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
fi
exec "$app_dir/usr/bin/Andromeld.Desktop" "$@"
EOF
chmod +x "$app_dir/AppRun"
desktop-file-validate "$app_dir/andromeld.desktop"

# Run the packaging tool without FUSE, including in CI and containers.
APPIMAGE_EXTRACT_AND_RUN=1 ARCH=x86_64 "$appimagetool" --no-appstream \
    "$app_dir" "$build_dir/AndroMeld-x86_64.AppImage"
chmod +x "$build_dir/AndroMeld-x86_64.AppImage"
mv -- "$build_dir/AndroMeld-x86_64.AppImage" "$output_dir/AndroMeld-x86_64.AppImage"
printf 'AppImage: %s/AndroMeld-x86_64.AppImage\n' "$output_dir"
