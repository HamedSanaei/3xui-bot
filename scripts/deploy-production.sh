#!/usr/bin/env bash
set -Eeuo pipefail

CANONICAL_REPO_URL="https://github.com/HamedSanaei/3xui-bot.git"
EXPECTED_LIVE_ROOT="/root/vpnetiran"
EXPECTED_SERVICE_NAME="vpnetiranbot.service"
STAGING_BASE="/root/.deploy/vpnetiran"
LOCK_FILE="/var/lock/vpnetiran-deploy.lock"
CURRENT_STAGE_ROOT=""

fail() {
  printf 'Deployment refused: %s\n' "$1" >&2
  exit 64
}

# Explicit status propagation preserves the fail-closed fix from 45fc9c5 even when callers use an OR-list.
# GNU timeout kills the entire child process group, including hung command descendants.
run_bounded() {
  local seconds="$1"
  shift
  [[ "$seconds" =~ ^[1-9][0-9]*$ ]] || fail "command timeout must be a positive number of seconds."
  timeout --signal=TERM --kill-after=10s "${seconds}s" "$@" 9>&- || return $?
}

REQUIRED_TUTORIAL_ASSET_DIRS=(android_v2rayng windows_v2rayn ios_android_v2box)
assert_tutorial_assets() {
  local publish_root="$1" dir entry count
  for dir in "${REQUIRED_TUTORIAL_ASSET_DIRS[@]}"; do
    [[ -d "$publish_root/Assets/tutorials/$dir" ]] || fail "missing built-in tutorial directory: $dir."
    count=0
    for entry in "$publish_root/Assets/tutorials/$dir"/*; do
      [[ -f "$entry" && ! -L "$entry" ]] || continue
      case "${entry,,}" in *.jpg|*.jpeg|*.png) count=$((count + 1)) ;; esac
    done
    ((count > 0)) || fail "tutorial directory contains no images: $dir."
  done
}

# Synchronize only verified runtime files, never a checkout. Protect existing config/token/databases in any layout.
sync_publish() {
  local stage_publish="$1" live_publish="$2"
  run_bounded 120 rsync -a --delete --checksum --safe-links \
    --filter='P Data/***' --exclude='Data/' \
    --exclude='*.db' --exclude='*.db-*' \
    --exclude='configuration*.json' --exclude='appsettings*.json' \
    --exclude='*token*' --exclude='*secret*' --exclude='.env*' \
    --exclude='*.key' --exclude='*.pem' --exclude='*.pfx' \
    "$stage_publish/" "$live_publish/" || return $?
}

# This is the only pre-sync path used by production and the behavioral harness. Any verification/preflight failure
# returns before rsync/systemd. MigrationPreflight uses SQLite online backups into temporary databases, never live writes.
install_verified_release() {
  local archive="$1" sha="$2" checksum="$3" stage_publish="$4" live_publish="$5" service="$6" verifier="$7"
  run_bounded 120 python3 "$verifier" verify "$archive" "$sha" "$checksum" --extract "$stage_publish" || return $?
  assert_tutorial_assets "$stage_publish" || return $?
  run_bounded 180 "$stage_publish/Adminbot" --migration-check \
    --users-source "$live_publish/Data/users.db" \
    --credentials-source "$live_publish/Data/credentials.db" || return $?
  assert_data_unchanged || return $?
  sync_publish "$stage_publish" "$live_publish" || return $?
  assert_data_unchanged || return $?
  run_bounded 60 systemctl restart "$service" || return $?
}

main() {
  local deploy_sha="${1:-}" repo_url="${2:-}" run_id="${3:-}" run_attempt="${4:-}"
  local live_root="${5:-$EXPECTED_LIVE_ROOT}" service_name="${6:-$EXPECTED_SERVICE_NAME}"
  local checksum="${7:-}"
  [[ "$deploy_sha" =~ ^[0-9a-f]{40}$ ]] || fail "deployment SHA must be exactly 40 lowercase hexadecimal characters."
  [[ "$checksum" =~ ^[0-9a-f]{64}$ ]] || fail "artifact checksum must be exactly 64 lowercase hexadecimal characters."
  [[ "$run_id" =~ ^[0-9]+$ && "$run_attempt" =~ ^[0-9]+$ ]] || fail "run id and attempt must be numeric."
  [[ "$repo_url" == "$CANONICAL_REPO_URL" ]] || fail "repository URL does not match the canonical origin."
  [[ "$live_root" == "$EXPECTED_LIVE_ROOT" ]] || fail "live root must remain $EXPECTED_LIVE_ROOT."
  [[ "$service_name" == "$EXPECTED_SERVICE_NAME" ]] || fail "service name must remain $EXPECTED_SERVICE_NAME."
  local command
  for command in python3 rsync flock realpath stat systemctl journalctl timeout; do
    command -v "$command" >/dev/null || fail "$command is required on production."
  done
  local live_publish="$live_root/bin/Release/net10.0/linux-x64/publish"
  local live_data="$live_publish/Data"
  [[ -d "$live_publish" && -d "$live_data" && ! -L "$live_data" ]] || fail "protected production paths are missing or symlinked."
  local service_exec data_real data_identity
  run_bounded 15 systemctl cat "$service_name" >/dev/null || fail "systemd service cannot be read."
  service_exec="$(run_bounded 15 systemctl show "$service_name" -p ExecStart --value)" || fail "systemd ExecStart cannot be read."
  [[ "$service_exec" == *"$live_publish/Adminbot"* ]] || fail "systemd ExecStart does not point to the live Adminbot executable."
  data_real="$(realpath -e -- "$live_data")" || fail "Data cannot be resolved."
  [[ "$data_real" == "$live_data" ]] || fail "protected Data path resolves somewhere unexpected."
  data_identity="$(stat -Lc '%d:%i' -- "$live_data")" || fail "Data identity cannot be read."
  assert_data_unchanged() {
    [[ -d "$live_data" && ! -L "$live_data" ]] || fail "production Data disappeared or changed type."
    [[ "$(realpath -e -- "$live_data")" == "$data_real" ]] || fail "production Data path changed."
    [[ "$(stat -Lc '%d:%i' -- "$live_data")" == "$data_identity" ]] || fail "production Data directory was replaced."
  }
  exec 9>"$LOCK_FILE"
  flock -w 60 -x 9 || fail "production deployment lock remained busy for more than 60 seconds."
  local incoming="$STAGING_BASE/incoming/${deploy_sha}-${run_id}-${run_attempt}"
  local stage_root="$STAGING_BASE/${deploy_sha}-${run_id}-${run_attempt}"
  [[ "$(realpath -e -- "$incoming")" == "$incoming" && ! -L "$incoming" ]] || fail "incoming artifact path is unsafe."
  [[ "$(realpath -m -- "$stage_root")" == "$stage_root" && ! -e "$stage_root" ]] || fail "unique staging path is unsafe or already exists."
  mkdir -m 700 -- "$stage_root" || fail "cannot create staging directory."
  CURRENT_STAGE_ROOT="$stage_root"
  cleanup_stage() {
    rm -rf -- "$CURRENT_STAGE_ROOT"
  }
  trap cleanup_stage EXIT
  printf 'Installing verified runner artifact for %s (run %s attempt %s).\n' "$deploy_sha" "$run_id" "$run_attempt"
  install_verified_release "$incoming/release.tar.gz" "$deploy_sha" "$checksum" "$stage_root/publish" \
    "$live_publish" "$service_name" "$incoming/release-artifact.py" \
    || fail "artifact verification, copy-only migration preflight, synchronization or restart failed."
  local active=false
  for _ in {1..10}; do
    if run_bounded 10 systemctl is-active --quiet "$service_name"; then active=true; break; fi
    sleep 2
  done
  if [[ "$active" != true ]]; then
    run_bounded 15 journalctl -u "$service_name" --since "15 minutes ago" -n 300 --no-pager || true
    fail "service health verification failed after restart."
  fi
  assert_data_unchanged
  run_bounded 15 journalctl -u "$service_name" --since "5 minutes ago" -n 150 --no-pager || true
  printf 'Production deployment completed for commit %s.\n' "$deploy_sha"
}

if [[ "${BASH_SOURCE[0]:-$0}" == "$0" ]]; then
  main "$@"
fi
