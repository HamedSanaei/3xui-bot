#!/usr/bin/env bash
set -Eeuo pipefail

# Production deployment entry point. It is streamed over SSH by the GitHub Actions deploy job and consumes an
# already-built release artifact, so the production host never restores, builds, tests, or publishes anything.
#
# WHY THIS SHAPE
# The previous revision cloned the repository on the host and ran restore, build, the full test suite, both EF
# pending-model checks, and publish there. That made every release cost the server's CPU and disk for several minutes,
# and it made the deployment's correctness depend on the SDK installed on the production machine. The expensive
# verification work now happens on the GitHub runner, whose result is an immutable artifact plus its SHA-256 digest.
# Production keeps only the checks that must run against the real machine: artifact integrity, artifact completeness,
# migration preflight against the live databases, and the health of the restarted service.
#
# WHAT MUST NOT REGRESS
# Production state lives in the protected Data directory, which sits inside the live publish directory because the
# application resolves its paths relative to the process working directory (./Data/users.db, ./Data/configuration.json,
# ./Data/Logs/...). It therefore holds both SQLite databases, the deployment's only configuration.json, and the daily
# diagnostic logs. The switch below is a sequence of atomic renames in which the Data directory keeps its inode and is
# simply moved between two release parents, so no database, configuration file, or log file is ever copied, replaced,
# truncated, or deleted by a deployment.

CANONICAL_ARTIFACT_PREFIX="vpnetiranbot-release"
EXPECTED_LIVE_ROOT="/root/vpnetiran"
EXPECTED_SERVICE_NAME="vpnetiranbot.service"
STAGING_BASE="/root/.deploy/vpnetiran"
INCOMING_BASE="/root/.deploy/incoming"
# Exclusive deployment lock. The advisory flock lives on the open file description, which this shell shares with every
# child it starts, so the lock is released only once all of those processes have exited. A deploy that is killed while
# its migration preflight still runs therefore leaves the lock held by the surviving child - the wedge that was observed
# in production - which is why the wait below is bounded and the teardown below kills that child.
# On this host /var/lock is the systemd alias of /run/lock, so this is the same file an operator sees as
# /run/lock/vpnetiran-deploy.lock.
LOCK_FILE="/var/lock/vpnetiran-deploy.lock"
# Upper bound for waiting on that lock. An unbounded `flock -x` turned a held lock into a silent stall with no output at
# all, which GitHub's SSH client only reported minutes later as a broken pipe and exit 255, and it let one failed release
# block every later one. The wait is now capped and announced before it can block.
LOCK_WAIT_SECONDS=300
# Upper bound for a single migration preflight run. The preflight is the only long-running child this script starts, and
# it is the one operation that could otherwise keep the deploy - and with it the deployment lock - alive long after the
# GitHub-managed SSH session has gone away.
PREFLIGHT_TIMEOUT_SECONDS=300
CURRENT_STAGE_ROOT=""
PROTECTED_DATA_DIR=""
PROTECTED_DATA_REAL=""
PROTECTED_DATA_IDENTITY=""
PREFLIGHT_CHILD_PID=""

fail() {
  printf 'Deployment refused: %s\n' "$1" >&2
  exit 64
}

# Stops the migration preflight this deploy started, if it is still running. This is what prevents an interrupted deploy
# (dropped SSH session, cancelled workflow, expired step timeout) from leaving an orphaned child behind that keeps the
# deployment lock occupied and stalls every later release.
terminate_preflight_child() {
  local child_pid="$PREFLIGHT_CHILD_PID"
  [[ -n "$child_pid" ]] || return 0
  PREFLIGHT_CHILD_PID=""
  kill -TERM "$child_pid" 2>/dev/null || true
  local waited=0
  while kill -0 "$child_pid" 2>/dev/null && [[ "$waited" -lt 10 ]]; do
    sleep 1
    waited=$((waited + 1))
  done
  kill -KILL "$child_pid" 2>/dev/null || true
}

# Releases the lock explicitly rather than relying on the process exit alone. Closing the descriptor on exit already
# releases it, but releasing it here keeps the intent visible in the teardown path an operator reads. A descriptor that
# was never opened is ignored, so this is safe on failure paths that abort before the lock is taken.
release_deployment_lock() {
  flock -u 9 2>/dev/null || true
}

# ----------------------------------------------------------------------------------------------------------------------
# Protected production state
# ----------------------------------------------------------------------------------------------------------------------

# Captures the identity of the protected Data directory so every later step can prove it was neither replaced nor
# recreated. The identity is a device:inode pair, which survives a rename between release parents but changes if the
# directory is copied, deleted, or recreated.
protected_data_identity() {
  local live_data="$1"
  [[ -d "$live_data" ]] || fail "protected production Data directory is missing."
  [[ ! -L "$live_data" ]] || fail "protected production Data must be a real directory, not a symlink."
  PROTECTED_DATA_DIR="$live_data"
  PROTECTED_DATA_REAL="$(realpath -e -- "$live_data")" || fail "protected production Data path could not be resolved."
  [[ "$PROTECTED_DATA_REAL" == "$live_data" ]] || fail "protected Data path resolves somewhere unexpected."
  PROTECTED_DATA_IDENTITY="$(stat -Lc '%d:%i' -- "$live_data")"
}

assert_data_unchanged() {
  [[ -d "$PROTECTED_DATA_DIR" && ! -L "$PROTECTED_DATA_DIR" ]] || fail "protected production Data directory disappeared or changed type."
  [[ "$(realpath -e -- "$PROTECTED_DATA_DIR")" == "$PROTECTED_DATA_REAL" ]] || fail "protected production Data path identity changed."
  [[ "$(stat -Lc '%d:%i' -- "$PROTECTED_DATA_DIR")" == "$PROTECTED_DATA_IDENTITY" ]] || fail "protected production Data directory was replaced."
}

# ----------------------------------------------------------------------------------------------------------------------
# Artifact validation
# ----------------------------------------------------------------------------------------------------------------------

# Proves the transferred archive carries runtime files only. A release that shipped users.db, credentials.db,
# configuration.json, or any Data/ entry could overwrite or shadow production state once extracted, so such an archive is
# refused before it is unpacked.
assert_artifact_archive() {
  local archive="$1"
  local entries entry lower
  entries="$(tar -tzf "$archive")" || fail "release artifact is not a readable gzip tar archive."

  while IFS= read -r entry; do
    [[ -n "$entry" ]] || continue
    lower="${entry,,}"
    case "$lower" in
      data|data/*|./data|./data/*)
        fail "release artifact contains protected state (Data/) and will not be deployed." ;;
      # *.db-* already refuses the SQLite sidecars of a live database (-wal, -shm, -journal), so listing
      # them separately would only repeat a refusal this pattern has already made.
      *.db|*.db-*|*configuration.json)
        fail "release artifact contains a database or configuration file and will not be deployed." ;;
      .git|.git/*|./.git|./.git/*)
        fail "release artifact contains version-control metadata and will not be deployed." ;;
    esac
  done <<< "$entries"

  grep -Eq '(^|/)Adminbot$' <<< "$entries" || fail "release artifact does not contain the Adminbot executable."
  grep -Eq '(^|/)Adminbot\.dll$' <<< "$entries" || fail "release artifact does not contain Adminbot.dll."
}

# Proves the extracted release is complete enough to run. This runs after extraction and before the switch, so an
# incomplete artifact can never replace a working release.
assert_release_complete() {
  local publish_root="$1"
  [[ -f "$publish_root/Adminbot" ]] || fail "staged release is missing the Adminbot executable."
  [[ -f "$publish_root/Adminbot.dll" ]] || fail "staged release is missing Adminbot.dll."
  [[ -f "$publish_root/Assets/telegram-ui/emoji-map.json" ]] \
    || fail "staged release is missing the Telegram premium-UI emoji asset."
}

# Built-in installation tutorials are shipped as ordinary publish files beside the application. The tenant customer flow
# exposes the tutorial buttons unconditionally, so a release whose images were never copied would ship a guide that fails
# at send time. This preflight inspects ONLY the staged publish artifact - never the developer source tree - so it proves
# both that the asset copy rules work and that the artifact about to be activated is complete.
REQUIRED_TUTORIAL_ASSET_DIRS=(
  "android_v2rayng"
  "windows_v2rayn"
  "ios_android_v2box"
)

assert_tutorial_assets() {
  local publish_root="$1"
  local tutorial_root="$publish_root/Assets/tutorials"
  local dir target entry image_count

  [[ -d "$tutorial_root" ]] || fail "published artifact is missing Assets/tutorials."
  for dir in "${REQUIRED_TUTORIAL_ASSET_DIRS[@]}"; do
    target="$tutorial_root/$dir"
    [[ -d "$target" ]] || fail "published artifact is missing the built-in tutorial directory Assets/tutorials/$dir."
    image_count=0
    for entry in "$target"/*; do
      [[ -f "$entry" ]] || continue
      case "${entry,,}" in
        *.jpg|*.jpeg|*.png) image_count=$((image_count + 1)) ;;
      esac
    done
    if ((image_count == 0)); then
      fail "published tutorial directory Assets/tutorials/$dir contains no supported image files."
    fi
  done
}

# ----------------------------------------------------------------------------------------------------------------------
# Atomic release switch
# ----------------------------------------------------------------------------------------------------------------------

# Swaps a staged release into the live publish path and carries the protected Data directory across unchanged.
#
# The four steps below are renames inside one filesystem, so none of them copies a byte:
#   1. the live release moves aside to the rollback slot;
#   2. the staged release becomes the live release;
#   3. the protected Data directory is renamed from the rollback slot into the new live release, keeping its inode;
#   4. the rollback slot without Data is discarded by the caller after a healthy deployment.
# The service is never restarted by this function and never observes a partially installed tree, which is what makes the
# switch atomic from the running process's point of view.
activate_release() {
  local staged="$1"
  local live_publish="$2"
  local slot parent_device staged_device

  [[ -d "$staged" ]] || fail "staged release directory does not exist."
  [[ -d "$live_publish" ]] || fail "live publish directory does not exist."
  slot="$(dirname -- "$live_publish")"
  parent_device="$(stat -Lc '%d' -- "$slot")"
  staged_device="$(stat -Lc '%d' -- "$staged")"
  if [[ "$parent_device" != "$staged_device" ]]; then
    fail "staged release and live publish directory are on different filesystems, so the switch could not be atomic."
  fi

  rm -rf -- "${live_publish}.next" "${live_publish}.failed"
  mv -- "$staged" "${live_publish}.next"
  mv -- "$live_publish" "${live_publish}.prev"
  mv -- "${live_publish}.next" "$live_publish"
  mv -- "${live_publish}.prev/Data" "$live_publish/Data"
}

# Restores the previous release after a failed health check. The new release and the protected Data directory are swapped
# back in the reverse order, so Data again keeps its inode and the previously healthy payload is live before the service
# is restarted a second time.
rollback_release() {
  local live_publish="$1"
  [[ -d "${live_publish}.prev" ]] || fail "no previous release is retained, so a rollback is not possible."

  rm -rf -- "${live_publish}.failed"
  mv -- "$live_publish" "${live_publish}.failed"
  mv -- "${live_publish}.prev" "$live_publish"
  mv -- "${live_publish}.failed/Data" "$live_publish/Data"
}

# ----------------------------------------------------------------------------------------------------------------------
# Migration preflight and service health
# ----------------------------------------------------------------------------------------------------------------------

# Runs one migration preflight under a hard time limit while remembering its pid, so the teardown trap can stop it.
# The background/`wait` pair exists only to make that pid killable; the returned status is still the preflight's own, and
# a preflight that outlives its budget is reported the same way as a preflight that fails.
#
# The preflight is the only step of a deployment that can block indefinitely on the published application, so bounding it
# here is what guarantees the deploy process always reaches its teardown and therefore always releases the lock.
run_bounded_preflight() {
  local publish_root="$1"
  shift
  timeout --signal=TERM --kill-after=30 "$PREFLIGHT_TIMEOUT_SECONDS" \
    "$publish_root/Adminbot" --migration-check "$@" &
  PREFLIGHT_CHILD_PID=$!
  local status=0
  wait "$PREFLIGHT_CHILD_PID" || status=$?
  PREFLIGHT_CHILD_PID=""
  return "$status"
}

# Runs the published executable's own migration preflight. It starts no web server, Telegram receiver, or background
# worker, and it reads the live databases only through online-backup copies, so a schema change that cannot apply to real
# production data aborts the deployment before the switch.
run_migration_preflight() {
  local publish_root="$1"
  local live_data="$2"

  printf 'Running migration preflight against fresh databases.\n'
  run_bounded_preflight "$publish_root" \
    || fail "migration preflight failed or exceeded ${PREFLIGHT_TIMEOUT_SECONDS}s against fresh databases."
  printf 'Running migration preflight against production database copies.\n'
  run_bounded_preflight "$publish_root" \
    --users-source "$live_data/users.db" \
    --credentials-source "$live_data/credentials.db" \
    || fail "migration preflight failed or exceeded ${PREFLIGHT_TIMEOUT_SECONDS}s against production database copies."
}

# Shows a bounded slice of the service journal so a failed restart is diagnosable from the workflow log alone.
show_recent_journal() {
  local service_name="$1"
  journalctl -u "$service_name" --since "15 minutes ago" -n 300 --no-pager || true
}

# Restarts the service and waits for it to become active. Returns non-zero instead of exiting so the caller can decide
# between a rollback and a hard failure.
restart_and_verify() {
  local service_name="$1"

  if ! systemctl restart "$service_name"; then
    printf 'Service restart command failed. Showing bounded journal output.\n' >&2
    show_recent_journal "$service_name"
    return 1
  fi

  for _ in {1..10}; do
    if systemctl is-active --quiet "$service_name"; then
      return 0
    fi
    sleep 2
  done

  printf 'Service did not become active. Showing bounded journal output.\n' >&2
  show_recent_journal "$service_name"
  return 1
}

# ----------------------------------------------------------------------------------------------------------------------
# Entry point
# ----------------------------------------------------------------------------------------------------------------------

main() {
  local deploy_sha="${1:-}"
  local artifact_path="${2:-}"
  local artifact_digest="${3:-}"
  local run_id="${4:-manual}"
  local run_attempt="${5:-1}"
  local live_root="${6:-$EXPECTED_LIVE_ROOT}"
  local service_name="${7:-$EXPECTED_SERVICE_NAME}"

  [[ "$deploy_sha" =~ ^[0-9a-fA-F]{40}$ ]] || fail "deployment SHA must be exactly 40 hexadecimal characters."
  [[ "$artifact_digest" =~ ^[0-9a-fA-F]{64}$ ]] || fail "artifact digest must be exactly 64 hexadecimal characters."
  [[ "$run_id" =~ ^[0-9]+$ || "$run_id" == "manual" ]] || fail "GitHub run id must be numeric."
  [[ "$run_attempt" =~ ^[0-9]+$ ]] || fail "GitHub run attempt must be numeric."
  [[ "$live_root" == "$EXPECTED_LIVE_ROOT" ]] || fail "live root must remain $EXPECTED_LIVE_ROOT."
  [[ "$service_name" == "$EXPECTED_SERVICE_NAME" ]] || fail "service name must remain $EXPECTED_SERVICE_NAME."

  # Every exit path runs the teardown: a clean success, a refusal, a TERM from a cancelled workflow, or a HUP from a
  # dropped SSH session. Without the signal traps an interrupted deploy would leave its migration preflight running and
  # the deployment lock occupied, which is exactly the wedge that stalled later releases.
  teardown() {
    terminate_preflight_child
    if [[ -n "$CURRENT_STAGE_ROOT" && -e "$CURRENT_STAGE_ROOT" ]]; then
      local cleanup_target
      cleanup_target="$(realpath -m -- "$CURRENT_STAGE_ROOT")"
      if [[ "$cleanup_target" == "$STAGING_BASE/"* ]]; then
        rm -rf -- "$CURRENT_STAGE_ROOT"
      else
        printf 'Refusing unsafe staging cleanup: %s\n' "$cleanup_target" >&2
      fi
    fi
    release_deployment_lock
  }
  on_exit() {
    local exit_status=$?
    teardown
    exit "$exit_status"
  }
  trap on_exit EXIT
  trap 'exit 130' INT
  trap 'exit 143' TERM
  trap 'exit 129' HUP

  # Only tools that exist for validation, locking, service control, and bounding the preflight are required. No compiler,
  # SDK, or package restore is needed because the release arrives fully built.
  command -v tar >/dev/null || fail "tar is required on the production host."
  command -v flock >/dev/null || fail "flock is required on the production host."
  command -v timeout >/dev/null || fail "timeout is required on the production host to bound the migration preflight."
  command -v realpath >/dev/null || fail "realpath is required on the production host."
  command -v stat >/dev/null || fail "stat is required on the production host."
  command -v sha256sum >/dev/null || fail "sha256sum is required on the production host."
  command -v systemctl >/dev/null || fail "systemctl is required on the production host."
  command -v journalctl >/dev/null || fail "journalctl is required on the production host."
  command -v dotnet >/dev/null || fail "the .NET runtime is required on the production host to run the migration preflight."
  dotnet --list-runtimes | awk '{print $1, $2}' | grep -Eq '^Microsoft\.NETCore\.App 10\.' \
    || fail ".NET 10 runtime is required to run the published net10.0 application."

  [[ -f "$artifact_path" ]] || fail "release artifact does not exist on the production host."
  local artifact_real incoming_real
  artifact_real="$(realpath -e -- "$artifact_path")" || fail "release artifact path could not be resolved."
  incoming_real="$(realpath -m -- "$INCOMING_BASE")"
  [[ "$artifact_real" == "$incoming_real/"* ]] || fail "release artifact must be transferred into $INCOMING_BASE."
  [[ "$(basename -- "$artifact_real")" == "$CANONICAL_ARTIFACT_PREFIX.tar.gz" ]] \
    || fail "release artifact must be named $CANONICAL_ARTIFACT_PREFIX.tar.gz."
  [[ "$(basename -- "$(dirname -- "$artifact_real")")" == "$deploy_sha" ]] \
    || fail "release artifact directory must be named after the exact deployment commit."

  local live_publish="$live_root/bin/Release/net10.0/linux-x64/publish"
  local live_data="$live_publish/Data"
  [[ -d "$live_root" ]] || fail "live root does not exist."
  [[ -d "$live_publish" ]] || fail "live publish directory does not exist."
  systemctl cat "$service_name" >/dev/null || fail "systemd service does not exist or cannot be read."
  local service_exec
  service_exec="$(systemctl show "$service_name" -p ExecStart --value)"
  [[ "$service_exec" == *"$live_publish/Adminbot"* ]] || fail "systemd ExecStart does not point to the expected live Adminbot executable."

  protected_data_identity "$live_data"
  assert_data_unchanged

  mkdir -p "$STAGING_BASE"
  printf 'Acquiring the server-side deployment lock (giving up after %ss).\n' "$LOCK_WAIT_SECONDS"
  exec 9>"$LOCK_FILE"
  if ! flock -w "$LOCK_WAIT_SECONDS" -x 9; then
    fail "another deployment is already running: $LOCK_FILE stayed locked for ${LOCK_WAIT_SECONDS}s."
  fi
  printf 'Server-side deployment lock acquired.\n'

  local stage_root="$STAGING_BASE/${deploy_sha}-${run_id}-${run_attempt}"
  local stage_publish="$stage_root/publish"
  local canonical_stage
  canonical_stage="$(realpath -m -- "$stage_root")"
  [[ "$canonical_stage" == "$STAGING_BASE/"* ]] || fail "staging path escaped the validated deployment root."
  [[ ! -e "$stage_root" ]] || fail "unique staging directory already exists."
  CURRENT_STAGE_ROOT="$stage_root"

  # Artifact integrity is verified before anything is extracted, so a truncated or substituted transfer is refused
  # before it can reach the filesystem the service reads.
  printf 'Verifying release artifact digest for commit %s.\n' "$deploy_sha"
  local actual_digest
  actual_digest="$(sha256sum -- "$artifact_real" | awk '{print $1}')"
  [[ "${actual_digest,,}" == "${artifact_digest,,}" ]] || fail "release artifact digest does not match the GitHub runner's digest."
  assert_artifact_archive "$artifact_real"
  printf 'Release artifact integrity and content checks passed.\n'

  mkdir -p "$stage_publish"
  printf 'Extracting verified release artifact %s.\n' "$deploy_sha"
  tar -xzf "$artifact_real" -C "$stage_publish" --no-same-owner

  # The extracted tree is re-checked for protected state. The archive entry list above already refuses Data/, a database,
  # or a configuration file, so this is defense in depth against an archive that hides entries behind a longer path.
  if [[ -e "$stage_publish/Data" ]]; then
    fail "staged release unexpectedly contains a Data directory."
  fi
  assert_release_complete "$stage_publish"
  printf 'Verifying built-in tutorial assets in the staged release.\n'
  assert_tutorial_assets "$stage_publish"
  printf 'Built-in tutorial assets verified in the staged release.\n'

  chmod +x "$stage_publish/Adminbot" || fail "staged Adminbot executable could not be marked executable."
  run_migration_preflight "$stage_publish" "$live_data"
  assert_data_unchanged

  printf 'Activating the staged release with an atomic switch.\n'
  activate_release "$stage_publish" "$live_publish"
  assert_data_unchanged
  printf 'Release activated. Restarting %s.\n' "$service_name"

  if ! restart_and_verify "$service_name"; then
    printf 'Health verification failed after activation. Rolling back to the previous release.\n' >&2
    rollback_release "$live_publish"
    assert_data_unchanged
    if restart_and_verify "$service_name"; then
      fail "deployment of commit $deploy_sha failed and the previous release was restored."
    fi
    fail "deployment of commit $deploy_sha failed and the previous release could not be restarted either; operator action required."
  fi

  assert_data_unchanged
  # One previous release is retained as the rollback slot; a failed attempt directory from an earlier run is discarded now
  # that the service is verified healthy.
  rm -rf -- "${live_publish}.failed"
  rm -f -- "$artifact_real"
  printf 'Deployment health check passed for commit %s. Recent bounded service logs follow.\n' "$deploy_sha"
  journalctl -u "$service_name" --since "5 minutes ago" -n 150 --no-pager || true
  printf 'Production deployment completed for commit %s using a prebuilt GitHub artifact.\n' "$deploy_sha"
}

if [[ "${BASH_SOURCE[0]:-$0}" == "$0" ]]; then
  main "$@"
fi
