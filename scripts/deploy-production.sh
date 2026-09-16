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
# Upper bound for waiting until the restarted service has reported the database paths it resolved. The application logs
# both paths unconditionally as its first startup lines, so this window only absorbs journald flush latency; exhausting it
# means the deployment cannot prove persistence health and must not declare success.
SERVICE_REPORT_ATTEMPTS=15
SERVICE_REPORT_DELAY_SECONDS=2
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

# Reports whether a directory can only be the protected production Data directory: a real directory, never a symlink,
# holding at least one SQLite database file. An empty scaffolding directory must never qualify, because mistaking one for
# production state is the accident this whole deployment flow exists to prevent.
is_protected_data_candidate() {
  local candidate="$1"
  [[ -d "$candidate" && ! -L "$candidate" ]] || return 1
  local entry
  for entry in "$candidate"/*; do
    case "$entry" in
      *.db|*.db-wal|*.db-shm) [[ -f "$entry" ]] && return 0 ;;
    esac
  done
  return 1
}

# Verifies the protected state a switch depends on before anything moves: the Data directory with both production
# databases exists and is usable, and the directories involved in the renames can be traversed and written.
#
# Why this exists: the application resolves its database paths to absolute values once at startup and then keeps running
# while every access fails, so a release that reaches production without its Data directory does not crash - it degrades
# into SQLite error 14 ("unable to open database file") for every worker. Missing or empty database files are the same
# class of accident: SQLite would happily create them and the bot would appear to have lost all of its state.
#
# The deployment runs as root, so the permission checks below catch a release tree whose ownership or mode changed rather
# than acting as a security boundary.
assert_protected_data_ready() {
  local live_publish="$1"
  local live_data="$2"
  local database_name

  [[ -d "$live_data" && ! -L "$live_data" ]] || fail "protected Data directory is missing or is not a real directory."
  [[ -x "$live_data" ]] || fail "protected Data directory cannot be traversed."
  [[ -w "$live_data" ]] || fail "protected Data directory is not writable, so the application could not persist state."

  for database_name in users.db credentials.db; do
    [[ -f "$live_data/$database_name" ]] || fail "protected Data directory is missing $database_name."
    [[ -r "$live_data/$database_name" ]] || fail "protected $database_name is not readable."
    [[ -s "$live_data/$database_name" ]] || fail "protected $database_name is empty, which SQLite would treat as a fresh database."
  done

  # The switch renames the live release and the staged release inside the release parent, so that parent has to stay
  # writable and traversable for the switch to remain a rename rather than a copy.
  [[ -d "$live_publish" && -x "$live_publish" ]] || fail "live publish directory cannot be traversed."
  [[ -w "$(dirname -- "$live_publish")" ]] || fail "release parent directory is not writable, so a switch could not be atomic."
}

# Prints the identity the deployment just validated: the service account systemd will run, and the mode, owner, and size of
# the protected directory, both production databases, and the release parent. Why it exists: every check above runs as
# root, so its permission tests cannot distinguish a tree the service account can write from one only root can write. This
# evidence is captured in the workflow log on every release, which is what makes a permissions question answerable after
# the fact instead of requiring an interactive look at the host.
show_protected_state_details() {
  local live_publish="$1"
  local live_data="$2"
  local service_name="$3"
  local service_user database_name

  service_user="$(systemctl show "$service_name" -p User --value 2>/dev/null || true)"
  [[ -n "$service_user" ]] || service_user="(unset; systemd runs the unit as root)"
  printf 'Protected state identity (service account: %s):\n' "$service_user"
  stat -Lc '  %n type=%F mode=%A owner=%U:%G size=%s inode=%i' \
    -- "$live_data" "$(dirname -- "$live_publish")" 2>/dev/null || true
  for database_name in users.db credentials.db; do
    [[ -e "$live_data/$database_name" ]] || continue
    stat -Lc '  %n type=%F mode=%A owner=%U:%G size=%s inode=%i' -- "$live_data/$database_name" 2>/dev/null || true
  done
}

# Restores the protected Data directory to the live release after a switch that could not finish carrying it across.
#
# Why this exists: a switch moves the live release aside first and renames Data into the new release afterwards. If that
# sequence is interrupted in between - or if an earlier revision nested the live release inside the rollback slot - the live
# release is left without Data, and the application would then start against newly created, empty databases.
#
# The repair deliberately refuses to guess: it acts only when exactly one plausible candidate is found among the retained
# slots, and reports ambiguity as a refusal instead of picking one of several candidates.
recover_stranded_protected_data() {
  local live_publish="$1"
  local live_data="$live_publish/Data"
  local slot_base candidate found="" matches=0

  # A path that already exists is left to protected_data_identity to judge; this only fills in a missing directory.
  if [[ -e "$live_data" ]]; then
    return 0
  fi

  slot_base="$(basename -- "$live_publish")"
  for candidate in \
    "$live_publish.prev/Data" \
    "$live_publish.prev/$slot_base/Data" \
    "$live_publish.failed/Data" \
    "$live_publish.failed/$slot_base/Data"; do
    is_protected_data_candidate "$candidate" || continue
    found="$candidate"
    matches=$((matches + 1))
  done

  case "$matches" in
    0)
      # Nothing to repair here; the ordinary Data validation reports the missing directory.
      return 0
      ;;
    1)
      printf 'Recovering the protected Data directory from %s.\n' "$found"
      mv -- "$found" "$live_data" || fail "protected Data directory could not be recovered into the live release."
      ;;
    *)
      fail "$matches candidate Data directories were found outside the live release; refusing to guess which one is production state."
      ;;
  esac
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
# The steps below are renames inside one filesystem, so none of them copies a byte:
#   1. the rollback slot is freed and whichever release still occupied it is parked under a temporary name, because
#      `mv live slot` onto an existing directory nests the live release inside the slot instead of replacing it;
#   2. the live release moves aside to the rollback slot;
#   3. the staged release becomes the live release;
#   4. the protected Data directory is renamed from the rollback slot into the new live release, keeping its inode;
#   5. the parked release is discarded, and the rollback slot - now without Data - is retained by the caller as the
#      rollback point until the next deployment replaces it.
# The service is never restarted by this function and never observes a partially installed tree, which is what makes the
# switch atomic from the running process's point of view.
activate_release() {
  local staged="$1"
  local live_publish="$2"
  local slot parent_device staged_device
  local next_slot prev_slot failed_slot displaced_slot

  [[ -d "$staged" ]] || fail "staged release directory does not exist."
  [[ -d "$live_publish" ]] || fail "live publish directory does not exist."
  slot="$(dirname -- "$live_publish")"
  parent_device="$(stat -Lc '%d' -- "$slot")"
  staged_device="$(stat -Lc '%d' -- "$staged")"
  if [[ "$parent_device" != "$staged_device" ]]; then
    fail "staged release and live publish directory are on different filesystems, so the switch could not be atomic."
  fi
  next_slot="${live_publish}.next"
  prev_slot="${live_publish}.prev"
  failed_slot="${live_publish}.failed"
  displaced_slot="${live_publish}.prev.displaced"

  # The Data directory is carried across from the release being replaced, so that release has to own it. The caller proves
  # the same thing through protected_data_identity; repeating it here keeps the guarantee next to the move that needs it.
  [[ -d "$live_publish/Data" ]] || fail "the live release does not contain the protected Data directory, so nothing could be carried across the switch."

  # The protected Data directory is moved into the release activated below, and `mv directory existing-directory` nests the
  # source inside the destination instead of replacing it. A release that already contained a Data directory - shipped that
  # way in the artifact, or touched by a service restart or an operator run against the staged tree - would therefore bury
  # production state one level too deep and leave the live release with an empty Data directory, which the application
  # repopulates with fresh databases. The same invariant is re-checked immediately before the move to cover that window.
  [[ ! -e "$staged/Data" ]] || fail "the staged release already contains a Data directory, so production state could not be moved into place without nesting it."

  rm -rf -- "$next_slot" "$failed_slot" "$displaced_slot"
  mv -- "$staged" "$next_slot"

  # The rollback slot must be free before the live release moves into it. Leaving a retained slot in place made
  # `mv live slot` nest the live release inside it, which put Data at slot/live/Data, pointed the move below at a path
  # that no longer existed, and stranded the new release with no Data at all.
  #
  # The displaced release is parked instead of deleted and is only discarded once the protected Data has been carried into
  # the new live release, so a slot that still held the only copy of that Data survives a switch that fails midway.
  if [[ -e "$prev_slot" ]]; then
    # A slot is only ever discarded when the live release demonstrably holds production database files of its own. Without
    # that proof the switch would delete what could be the only copy of production data and leave the application to start
    # against empty databases, so the situation is reported as a refusal for an operator to inspect instead.
    is_protected_data_candidate "$live_publish/Data" \
      || fail "$prev_slot still contains production state while the live release holds no database files; refusing to discard it."
    mv -- "$prev_slot" "$displaced_slot"
  fi
  mv -- "$live_publish" "$prev_slot"
  mv -- "$next_slot" "$live_publish"
  [[ ! -e "$live_publish/Data" ]] \
    || fail "the activated release already contains a Data directory; refusing to nest the protected production state inside it."
  [[ -d "$prev_slot/Data" ]] || fail "internal error: the release moved aside lost the protected Data directory during the switch."
  mv -- "$prev_slot/Data" "$live_publish/Data"
  rm -rf -- "$displaced_slot"
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

# Proves that a release can actually open the production databases, which a successful restart does not prove: systemd
# reports the unit as active while the application resolves its database paths to absolute values captured once at startup,
# so a release whose Data directory never arrived keeps running with every database access failing as SQLite error 14.
#
# The published executable's own non-serving preflight mode is used because it opens both production databases read-only
# through SQLite and copies them with the online backup API, so this check never modifies production data.
release_opens_databases() {
  local publish_root="$1"
  local live_data="$publish_root/Data"

  [[ -x "$publish_root/Adminbot" ]] || return 1
  is_protected_data_candidate "$live_data" || return 1
  "$publish_root/Adminbot" --migration-check \
    --users-source "$live_data/users.db" \
    --credentials-source "$live_data/credentials.db"
}

# Extracts the database paths a service instance reported for itself from raw journal text, keeping the last value seen for
# each database name so the newest startup wins over any earlier instance still inside the queried window.
#
# The parser is deliberately independent of the journal query so the matching rules can be tested against captured text.
reported_database_paths() {
  printf '%s\n' "$1" | awk '
    {
      marker = index($0, "[Database] ")
      if (marker == 0) next
      entry = substr($0, marker + 11)
      separator = index(entry, " path: ")
      if (separator == 0) next
      name = substr(entry, 1, separator - 1)
      path = substr(entry, separator + 7)
      sub(/\r$/, "", path)
      if (name == "" || path == "") next
      last[name] = path
    }
    END { for (name in last) printf "%s=%s\n", name, last[name] }
  ' | LC_ALL=C sort
}

# Reads the log of the service process that is running right now. It is filtered by the unit's current main pid so the
# answer describes this deployment's service instance rather than a previous one that is still inside the time window; a
# host whose systemd cannot report a pid falls back to a bounded time window.
service_startup_journal() {
  local service_name="$1"
  local main_pid

  main_pid="$(systemctl show "$service_name" -p MainPID --value 2>/dev/null || true)"
  if [[ "$main_pid" =~ ^[1-9][0-9]*$ ]]; then
    journalctl -u "$service_name" _PID="$main_pid" -n 200 --no-pager || true
    return 0
  fi
  journalctl -u "$service_name" --since "5 minutes ago" -n 200 --no-pager || true
}

# Proves that the restarted service resolved its own database paths inside the release this deployment just activated. It is
# the only check that closes the loop on the failure agent in SQLite error 14: because the application turns ./Data/users.db
# into an absolute path once at startup, a service can be running, can hold locks, and can still be reading a different
# release's directory after a switch, which systemd reports as a healthy unit.
#
# Returns non-zero instead of exiting so the caller can roll back, and reports every path it could not accept.
assert_service_reports_release_databases() {
  local service_name="$1"
  local live_publish="$2"
  local expected_data="$live_publish/Data"
  local attempts=0 reported entry reported_path

  while [[ "$attempts" -lt "$SERVICE_REPORT_ATTEMPTS" ]]; do
    reported="$(reported_database_paths "$(service_startup_journal "$service_name")")"
    if [[ -n "$reported" ]]; then
      printf 'Database paths reported by the running service:\n'
      while IFS= read -r entry; do
        printf '  %s\n' "$entry"
      done <<< "$reported"
      while IFS= read -r entry; do
        reported_path="${entry#*=}"
        if [[ "$reported_path" != "$expected_data/"* ]]; then
          printf 'The running service resolves %s to %s, which is outside the activated release (%s).\n' \
            "${entry%%=*}" "$reported_path" "$expected_data" >&2
          return 1
        fi
      done <<< "$reported"
      return 0
    fi
    attempts=$((attempts + 1))
    sleep "$SERVICE_REPORT_DELAY_SECONDS"
  done

  printf 'The restarted service reported no database path within %s seconds, so its persistence health could not be proven.\n' \
    "$((SERVICE_REPORT_ATTEMPTS * SERVICE_REPORT_DELAY_SECONDS))" >&2
  return 1
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

  mkdir -p "$STAGING_BASE"
  printf 'Acquiring the server-side deployment lock (giving up after %ss).\n' "$LOCK_WAIT_SECONDS"
  exec 9>"$LOCK_FILE"
  if ! flock -w "$LOCK_WAIT_SECONDS" -x 9; then
    fail "another deployment is already running: $LOCK_FILE stayed locked for ${LOCK_WAIT_SECONDS}s."
  fi
  printf 'Server-side deployment lock acquired.\n'

  # The protected Data directory is repaired and then identified inside the lock, so neither the repair nor the captured
  # identity can race a concurrent manual deployment. Everything the switch depends on is validated here, before any
  # artifact work, so a release that could not carry working production state is refused instead of installed.
  recover_stranded_protected_data "$live_publish"
  protected_data_identity "$live_data"
  assert_data_unchanged
  assert_protected_data_ready "$live_publish" "$live_data"
  show_protected_state_details "$live_publish" "$live_data" "$service_name"

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

  # A restarted unit is not yet a healthy deployment. The activated release must also prove that it can open the production
  # databases, and the running service must report that it resolved those same databases, because the failure this guards
  # against produces a service whose every worker logs SQLite error 14 while systemd still reports the unit as active.
  if ! restart_and_verify "$service_name" \
    || ! release_opens_databases "$live_publish" \
    || ! assert_service_reports_release_databases "$service_name" "$live_publish"; then
    printf 'Health verification failed after activation (service restart, production database access, or the service database paths). Rolling back to the previous release.\n' >&2
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
