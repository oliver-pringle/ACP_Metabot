#!/usr/bin/env bash
# Nightly SQLite backup for ACP_Metabot.
#
# Usage:
#   scripts/backup.sh                # take a backup, rotate, optionally push offsite
#   scripts/backup.sh --install-cron # install a cron entry on this host (run once)
#   scripts/backup.sh --once          # alias for the default
#
# Env (optional):
#   ACP_METABOT_DB        Path to the live SQLite db. Default: /root/ACP_Metabot/data/acp_metabot.db
#   ACP_METABOT_BACKUPS   Backup directory.            Default: /var/backups/acp_metabot
#   ACP_METABOT_RETAIN    Days of local backups to keep. Default: 14
#   RCLONE_REMOTE         If set, runs `rclone copy <backup> $RCLONE_REMOTE`
#                         after the local backup. e.g. b2:my-bucket/acp_metabot
#
# The backup uses SQLite's `.backup` command (online backup API) so it's safe
# while the API is writing.

set -euo pipefail

DB_PATH="${ACP_METABOT_DB:-/root/ACP_Metabot/data/acp_metabot.db}"
BACKUP_DIR="${ACP_METABOT_BACKUPS:-/var/backups/acp_metabot}"
RETAIN_DAYS="${ACP_METABOT_RETAIN:-14}"

install_cron() {
    local script_path
    script_path="$(readlink -f "$0")"
    local cron_line="0 3 * * * ${script_path} >> /var/log/acp_metabot_backup.log 2>&1"
    # Idempotent: don't add a second copy on rerun.
    if crontab -l 2>/dev/null | grep -Fq "${script_path}"; then
        echo "cron entry already installed for ${script_path}"
    else
        ( crontab -l 2>/dev/null; echo "${cron_line}" ) | crontab -
        echo "installed cron: ${cron_line}"
    fi
    if ! command -v sqlite3 >/dev/null; then
        echo "WARNING: sqlite3 is not installed. Run: apt-get install -y sqlite3"
    fi
}

if [[ "${1:-}" == "--install-cron" ]]; then
    install_cron
    exit 0
fi

# Sanity checks.
if [[ ! -f "${DB_PATH}" ]]; then
    echo "ERROR: db file not found at ${DB_PATH}" >&2
    exit 1
fi
if ! command -v sqlite3 >/dev/null; then
    echo "ERROR: sqlite3 CLI is not installed (apt-get install -y sqlite3)" >&2
    exit 1
fi

mkdir -p "${BACKUP_DIR}"
TS="$(date -u +%Y%m%d-%H%M%S)"
OUT="${BACKUP_DIR}/acp_metabot-${TS}.db"

# Online backup (concurrent-safe).
sqlite3 "${DB_PATH}" ".backup '${OUT}'"
gzip -9 "${OUT}"
echo "wrote ${OUT}.gz"

# Rotate: delete backups older than RETAIN_DAYS.
find "${BACKUP_DIR}" -maxdepth 1 -name 'acp_metabot-*.db.gz' -type f -mtime "+${RETAIN_DAYS}" -delete

# Optional offsite push.
if [[ -n "${RCLONE_REMOTE:-}" ]]; then
    if command -v rclone >/dev/null; then
        rclone copy "${OUT}.gz" "${RCLONE_REMOTE}"
        echo "pushed ${OUT}.gz -> ${RCLONE_REMOTE}"
    else
        echo "WARNING: RCLONE_REMOTE set but rclone is not installed; skipping offsite push" >&2
    fi
fi
