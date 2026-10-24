#!/usr/bin/env bash
set -euo pipefail

if [[ ${1:-} == --help || $# -ne 1 ]]; then
    echo "Usage: sudo $0 <sysroot-directory>"
    [[ ${1:-} == --help ]] && exit 0
    exit 2
fi

if [[ $(id -u) -ne 0 ]]; then
    echo "Run this script with sudo." >&2
    exit 1
fi
command -v debootstrap >/dev/null || { echo "debootstrap is required." >&2; exit 1; }

sysroot_dir=$(realpath -m -- "$1")
ubuntu_repo=${UBUNTU_MIRROR:-https://archive.ubuntu.com/ubuntu}
codename=${UBUNTU_CODENAME:-jammy}
components=${UBUNTU_COMPONENTS:-main universe}
keyring=/usr/share/keyrings/ubuntu-archive-keyring.gpg
read -r -a extra_packages <<< "${UBUNTU_EXTRA_PACKAGES:-}"

if [[ -e $sysroot_dir ]]; then
    echo "Sysroot path already exists: $sysroot_dir" >&2
    exit 1
fi
[[ -f $keyring ]] || { echo "Install ubuntu-keyring first: $keyring is missing." >&2; exit 1; }
mkdir -p -- "$(dirname -- "$sysroot_dir")"

debootstrap --variant=minbase --arch=amd64 --include=ca-certificates \
    --keyring="$keyring" --force-check-gpg \
    "$codename" "$sysroot_dir" "${ubuntu_repo%/}/"

rm -f -- "$sysroot_dir/etc/apt/sources.list"
rm -rf -- "$sysroot_dir/etc/apt/sources.list.d"
mkdir -p -- "$sysroot_dir/etc/apt/sources.list.d"
cat > "$sysroot_dir/etc/apt/sources.list.d/$codename.list" <<EOF
deb $ubuntu_repo $codename $components
deb $ubuntu_repo $codename-updates $components
deb $ubuntu_repo $codename-security $components
deb $ubuntu_repo $codename-backports $components
EOF

chroot "$sysroot_dir" apt-get update
chroot "$sysroot_dir" env DEBIAN_FRONTEND=noninteractive apt-get -y install \
    build-essential gcc g++ zlib1g-dev symlinks "${extra_packages[@]}"
chroot "$sysroot_dir" symlinks -cr /usr
chroot "$sysroot_dir" apt-get clean
rm -rf -- "$sysroot_dir/var/lib/apt/lists/"* "$sysroot_dir/tmp/"* "$sysroot_dir/var/tmp/"*
echo "Ubuntu $codename x86_64 sysroot: $sysroot_dir"
