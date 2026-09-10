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

sync_source() {
  local source_dir="$1"
  local live_root="$2"
  rsync -a --delete --checksum \
    --filter='P bin/Release/net10.0/linux-x64/publish/Data/***' \
    --exclude='.git/' \
    --exclude='bin/' \
    --exclude='obj/' \
    --exclude='Adminbot.Tests/bin/' \
    --exclude='Adminbot.Tests/obj/' \
    --exclude='Data/configuration.json' \
    --exclude='*.db' --exclude='*.db-*' \
    "$source_dir/" "$live_root/"
}

sync_publish() {
  local stage_publish="$1"
  local live_publish="$2"
  rsync -a --delete --checksum \
    --filter='P Data/***' \
    --exclude='Data/' \
    "$stage_publish/" "$live_publish/"
}

main() {
  local deploy_sha="${1:-}"
  local repo_url="${2:-}"
  local run_id="${3:-manual}"
  local run_attempt="${4:-1}"
  local live_root="${5:-$EXPECTED_LIVE_ROOT}"
  local service_name="${6:-$EXPECTED_SERVICE_NAME}"

  [[ "$deploy_sha" =~ ^[0-9a-fA-F]{40}$ ]] || fail "deployment SHA must be exactly 40 hexadecimal characters."
  [[ "$run_id" =~ ^[0-9]+$ ]] || fail "GitHub run id must be numeric."
  [[ "$run_attempt" =~ ^[0-9]+$ ]] || fail "GitHub run attempt must be numeric."
  [[ "$repo_url" == "$CANONICAL_REPO_URL" ]] || fail "repository URL does not match the canonical origin."
  [[ "$live_root" == "$EXPECTED_LIVE_ROOT" ]] || fail "live root must remain $EXPECTED_LIVE_ROOT."
  [[ "$service_name" == "$EXPECTED_SERVICE_NAME" ]] || fail "service name must remain $EXPECTED_SERVICE_NAME."
  command -v git >/dev/null || fail "git is required on the production host."
  command -v dotnet >/dev/null || fail "dotnet is required on the production host."
  command -v rsync >/dev/null || fail "rsync is required on the production host."
  command -v flock >/dev/null || fail "flock is required on the production host."
  command -v realpath >/dev/null || fail "realpath is required on the production host."
  command -v stat >/dev/null || fail "stat is required on the production host."
  command -v systemctl >/dev/null || fail "systemctl is required on the production host."
  command -v journalctl >/dev/null || fail "journalctl is required on the production host."
  dotnet --info >/dev/null || fail "dotnet --info failed on the production host."
  dotnet --list-sdks | awk '{print $1}' | grep -Eq '^10\.' || fail ".NET 10 SDK is required to publish net10.0."

  local live_publish="$live_root/bin/Release/net10.0/linux-x64/publish"
  local live_data="$live_publish/Data"
  [[ -d "$live_root" ]] || fail "live root does not exist."
  [[ -d "$live_publish" ]] || fail "live publish directory does not exist."
  [[ -d "$live_data" ]] || fail "protected production Data directory is missing."
  [[ ! -L "$live_data" ]] || fail "protected production Data must be a real directory, not a symlink."
  systemctl cat "$service_name" >/dev/null || fail "systemd service does not exist or cannot be read."
  local service_exec
  service_exec="$(systemctl show "$service_name" -p ExecStart --value)"
  [[ "$service_exec" == *"$live_publish/Adminbot"* ]] || fail "systemd ExecStart does not point to the expected live Adminbot executable."

  local data_real data_identity
  data_real="$(realpath -e -- "$live_data")"
  [[ "$data_real" == "$live_data" ]] || fail "protected Data path resolves somewhere unexpected."
  data_identity="$(stat -Lc '%d:%i' -- "$live_data")"

  assert_data_unchanged() {
    [[ -d "$live_data" && ! -L "$live_data" ]] || fail "protected production Data directory disappeared or changed type."
    [[ "$(realpath -e -- "$live_data")" == "$data_real" ]] || fail "protected production Data path identity changed."
    [[ "$(stat -Lc '%d:%i' -- "$live_data")" == "$data_identity" ]] || fail "protected production Data directory was replaced."
  }

  mkdir -p "$STAGING_BASE"
  exec 9>"$LOCK_FILE"
  if ! flock -x 9; then
    fail "could not acquire the server-side deployment lock."
  fi

  local stage_root="$STAGING_BASE/${deploy_sha}-${run_id}-${run_attempt}"
  local stage_source="$stage_root/source"
  local stage_publish="$stage_root/publish"
  local canonical_stage
  canonical_stage="$(realpath -m -- "$stage_root")"
  [[ "$canonical_stage" == "$STAGING_BASE/"* ]] || fail "staging path escaped the validated deployment root."
  [[ ! -e "$stage_root" ]] || fail "unique staging directory already exists."
  CURRENT_STAGE_ROOT="$stage_root"

  cleanup_stage() {
    if [[ -n "$CURRENT_STAGE_ROOT" && -e "$CURRENT_STAGE_ROOT" ]]; then
      local cleanup_target
      cleanup_target="$(realpath -m -- "$CURRENT_STAGE_ROOT")"
      if [[ "$cleanup_target" == "$STAGING_BASE/"* ]]; then
        rm -rf -- "$CURRENT_STAGE_ROOT"
      else
        printf 'Refusing unsafe staging cleanup: %s\n' "$cleanup_target" >&2
      fi
    fi
  }
  trap cleanup_stage EXIT

  mkdir -p "$stage_root"
  printf 'Cloning exact deployment source into staging.\n'
  git clone --no-checkout "$repo_url" "$stage_source"
  git -C "$stage_source" fetch --no-tags origin "$deploy_sha"
  git -C "$stage_source" checkout --detach "$deploy_sha"

  local actual_sha
  actual_sha="$(git -C "$stage_source" rev-parse HEAD)"
  [[ "$actual_sha" == "$deploy_sha" ]] || fail "fresh clone HEAD does not match requested GitHub SHA."
  [[ -z "$(git -C "$stage_source" status --porcelain)" ]] || fail "fresh staging checkout is unexpectedly dirty."

  mkdir -p "$stage_publish"
  printf 'Publishing verified commit %s in staging.\n' "$actual_sha"
  (
    cd "$stage_source"
    dotnet publish Adminbot.csproj -c Release -f net10.0 -r linux-x64 --self-contained false -o "$stage_publish"
  )

  [[ -x "$stage_publish/Adminbot" ]] || fail "staged publish is missing the Adminbot executable."
  assert_data_unchanged

  printf 'Synchronizing repository source into live tree.\n'
  sync_source "$stage_source" "$live_root"
  assert_data_unchanged

  printf 'Synchronizing staged publish into live publish directory.\n'
  sync_publish "$stage_publish" "$live_publish"
  assert_data_unchanged

  printf 'Restarting %s after successful synchronization.\n' "$service_name"
  if ! systemctl restart "$service_name"; then
    printf 'Service restart command failed. Showing bounded journal output.\n' >&2
    journalctl -u "$service_name" --since "15 minutes ago" -n 300 --no-pager || true
    fail "systemd restart failed after deployment."
  fi

  local active=false
  for _ in {1..10}; do
    if systemctl is-active --quiet "$service_name"; then
      active=true
      break
    fi
    sleep 2
  done
  if [[ "$active" != true ]]; then
    printf 'Service did not become active. Showing bounded journal output.\n' >&2
    journalctl -u "$service_name" --since "15 minutes ago" -n 300 --no-pager || true
    fail "service health verification failed after restart."
  fi

  assert_data_unchanged
  printf 'Deployment health check passed. Recent bounded service logs follow.\n'
  journalctl -u "$service_name" --since "5 minutes ago" -n 150 --no-pager || true
  printf 'Production deployment completed for commit %s.\n' "$deploy_sha"
}

if [[ "${BASH_SOURCE[0]:-$0}" == "$0" ]]; then
  main "$@"
fi
