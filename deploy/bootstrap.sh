#!/usr/bin/env bash
#
# bootstrap.sh — fresh Ubuntu 22.04 host preparation for ACP_Metabot.
#
# Idempotent: safe to re-run. Installs Docker CE + compose plugin from
# Docker's official apt repo (the distro-shipped docker.io is too old to
# reliably support compose v2 healthcheck conditions), git, and openssl.
#
# Run as a sudoer:   bash bootstrap.sh
# Or explicitly:     sudo bash bootstrap.sh
#
# After it finishes, log out and back in (or `newgrp docker`) so the
# current user picks up its new docker group membership without sudo.

set -euo pipefail

if [[ $EUID -ne 0 ]]; then
    if ! command -v sudo >/dev/null; then
        echo "[bootstrap] need root or sudo" >&2
        exit 1
    fi
    SUDO="sudo"
else
    SUDO=""
fi

USER_TO_ADD="${SUDO_USER:-$USER}"

echo "[bootstrap] target user: $USER_TO_ADD"
echo "[bootstrap] updating apt index"
$SUDO apt-get update -y

echo "[bootstrap] installing prerequisites"
$SUDO apt-get install -y --no-install-recommends \
    ca-certificates curl gnupg lsb-release git openssl

if ! [[ -f /etc/apt/keyrings/docker.gpg ]]; then
    echo "[bootstrap] adding Docker apt repo"
    $SUDO install -m 0755 -d /etc/apt/keyrings
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg \
        | $SUDO gpg --dearmor -o /etc/apt/keyrings/docker.gpg
    $SUDO chmod a+r /etc/apt/keyrings/docker.gpg
    arch="$(dpkg --print-architecture)"
    codename="$(. /etc/os-release && echo "$VERSION_CODENAME")"
    echo "deb [arch=$arch signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $codename stable" \
        | $SUDO tee /etc/apt/sources.list.d/docker.list >/dev/null
    $SUDO apt-get update -y
fi

echo "[bootstrap] installing docker-ce + compose plugin"
$SUDO apt-get install -y --no-install-recommends \
    docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

echo "[bootstrap] enabling docker on boot"
$SUDO systemctl enable docker
$SUDO systemctl start docker

if ! id -nG "$USER_TO_ADD" | grep -qw docker; then
    echo "[bootstrap] adding $USER_TO_ADD to docker group"
    $SUDO usermod -aG docker "$USER_TO_ADD"
    echo
    echo "[bootstrap] >>> log out and back in (or run: newgrp docker)"
    echo "[bootstrap]     before invoking docker without sudo."
fi

echo "[bootstrap] versions:"
docker --version
docker compose version
git --version

echo "[bootstrap] done."
