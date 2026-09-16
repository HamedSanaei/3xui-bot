#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=deploy-production.sh
# shellcheck disable=SC1091
source "$script_dir/deploy-production.sh"

test_root="$(mktemp -d)"
cleanup_test() {
  rm -rf -- "$test_root"
}
trap cleanup_test EXIT

deploy_script="$script_dir/deploy-production.sh"
live_publish="$test_root/live/bin/Release/net10.0/linux-x64/publish"
stage_publish="$test_root/stage/publish"
mkdir -p "$live_publish/Data" "$live_publish/Assets/tutorials" "$stage_publish/Assets/tutorials"

# Production state: the two databases, the only configuration.json, and the daily logs all live inside the protected Data
# directory. Every assertion below exists to prove a deployment cannot replace, empty, or recreate it.
printf 'persistent-production-state\n' > "$live_publish/Data/preserve.txt"
printf 'live-database\n' > "$live_publish/Data/users.db"
printf 'live-credentials\n' > "$live_publish/Data/credentials.db"
printf 'live-configuration\n' > "$live_publish/Data/configuration.json"
printf 'old-app\n' > "$live_publish/Adminbot"
printf 'old-app-dll\n' > "$live_publish/Adminbot.dll"
printf 'obsolete-app\n' > "$live_publish/obsolete.dll"

printf 'new-app\n' > "$stage_publish/Adminbot"
printf 'new-app-dll\n' > "$stage_publish/Adminbot.dll"
printf 'new-library\n' > "$stage_publish/new-library.dll"
mkdir -p "$stage_publish/Assets/telegram-ui"
printf '{}\n' > "$stage_publish/Assets/telegram-ui/emoji-map.json"

protected_data_identity "$live_publish/Data"
live_data_inode_before="$(stat -Lc '%i' -- "$live_publish/Data")"

activate_release "$stage_publish" "$live_publish"

# The switch is a set of renames, so Data keeps its inode: it was never copied, recreated, or truncated.
assert_data_unchanged
[[ "$(stat -Lc '%i' -- "$live_publish/Data")" == "$live_data_inode_before" ]]
[[ "$(cat "$live_publish/Data/preserve.txt")" == "persistent-production-state" ]]
[[ "$(cat "$live_publish/Data/users.db")" == "live-database" ]]
[[ "$(cat "$live_publish/Data/credentials.db")" == "live-credentials" ]]
[[ "$(cat "$live_publish/Data/configuration.json")" == "live-configuration" ]]

# The new release is complete and contains no leftover file from the previous one.
[[ "$(cat "$live_publish/Adminbot")" == "new-app" ]]
[[ -f "$live_publish/new-library.dll" ]]
[[ ! -e "$live_publish/obsolete.dll" ]]

# The previous release is retained without Data, which is what makes a rollback possible while Data stays unique.
[[ -d "${live_publish}.prev" ]]
[[ -f "${live_publish}.prev/Adminbot" ]]
[[ ! -e "${live_publish}.prev/Data" ]]
printf 'Production atomic-switch and Data preservation test: PASS\n'
printf '  preserved Data content and inode across the switch\n'
printf '  installed the new release payload and removed stale files\n'
printf '  retained one previous release as the rollback slot\n'

rollback_release "$live_publish"

assert_data_unchanged
[[ "$(stat -Lc '%i' -- "$live_publish/Data")" == "$live_data_inode_before" ]]
[[ "$(cat "$live_publish/Adminbot")" == "old-app" ]]
[[ "$(cat "$live_publish/Data/users.db")" == "live-database" ]]
[[ ! -e "$live_publish/new-library.dll" ]]
printf 'Production rollback test: PASS\n'
printf '  restored the previous release payload\n'
printf '  carried the same Data inode back with it\n'

# ----------------------------------------------------------------------------------------------------------------------
# Repeat deployment
# ----------------------------------------------------------------------------------------------------------------------

# Every healthy deployment retains the rollback slot on purpose, and that slot never holds Data. So a second switch has to
# take the slot over. It could not before: `mv live slot` onto the already existing slot nested the live release inside
# it, which put Data at slot/live/Data, pointed the Data move at slot/Data, and left the newly activated release with no
# protected Data at all - the exact production failure this test now pins down.
repeat_root="$test_root/repeat"
repeat_live="$repeat_root/bin/Release/net10.0/linux-x64/publish"
repeat_stage_one="$repeat_root/stage-one"
repeat_stage_two="$repeat_root/stage-two"
mkdir -p "$repeat_live/Data" "$repeat_stage_one" "$repeat_stage_two"
printf 'live-database\n' > "$repeat_live/Data/users.db"
printf 'live-credentials\n' > "$repeat_live/Data/credentials.db"
printf 'first-release\n' > "$repeat_stage_one/Adminbot"
printf 'second-release\n' > "$repeat_stage_two/Adminbot"

protected_data_identity "$repeat_live/Data"
repeat_data_inode="$(stat -Lc '%i' -- "$repeat_live/Data")"

activate_release "$repeat_stage_one" "$repeat_live"
# Left exactly as a healthy deployment leaves it: the previous release retained without Data.
[[ -d "${repeat_live}.prev" && ! -e "${repeat_live}.prev/Data" ]]

activate_release "$repeat_stage_two" "$repeat_live"

[[ "$(cat "$repeat_live/Adminbot")" == "second-release" ]]
[[ "$(stat -Lc '%i' -- "$repeat_live/Data")" == "$repeat_data_inode" ]]
[[ "$(cat "$repeat_live/Data/users.db")" == "live-database" ]]
[[ "$(cat "$repeat_live/Data/credentials.db")" == "live-credentials" ]]
[[ "$(cat "${repeat_live}.prev/Adminbot")" == "first-release" ]]
[[ ! -e "${repeat_live}.prev/Data" ]]
[[ ! -e "${repeat_live}.prev/$(basename -- "$repeat_live")" ]]
[[ ! -e "${repeat_live}.prev.displaced" ]]
printf 'Production repeat-deployment test: PASS\n'
printf '  took over the retained rollback slot instead of nesting the live release inside it\n'
printf '  carried the protected Data into the second release with its inode intact\n'

# A switch interrupted between moving the live release aside and moving Data back leaves the live release without Data. The
# repair must restore it, and must refuse rather than guess when several candidates could be production state.
recover_root="$test_root/recover"
recover_live="$recover_root/bin/Release/net10.0/linux-x64/publish"
recover_nested="${recover_live}.prev/$(basename -- "$recover_live")/Data"
mkdir -p "$recover_live" "$recover_nested"
printf 'recovered-database\n' > "$recover_nested/users.db"
printf 'old-release\n' > "${recover_live}.prev/Adminbot"
recover_stranded_protected_data "$recover_live"
[[ "$(cat "$recover_live/Data/users.db")" == "recovered-database" ]]
printf 'Production stranded-Data recovery test: PASS\n'
printf '  restored the protected Data directory from the retained slot\n'

recover_ambiguous_live="$test_root/recover-ambiguous/bin/Release/net10.0/linux-x64/publish"
mkdir -p "$recover_ambiguous_live" "${recover_ambiguous_live}.prev/Data" "${recover_ambiguous_live}.failed/Data"
printf 'first-candidate\n' > "${recover_ambiguous_live}.prev/Data/users.db"
printf 'second-candidate\n' > "${recover_ambiguous_live}.failed/Data/users.db"
if ( recover_stranded_protected_data "$recover_ambiguous_live" ) 2>/dev/null; then
  echo "Expected the stranded-Data repair to refuse an ambiguous layout." >&2
  exit 1
fi
[[ ! -e "$recover_ambiguous_live/Data" ]]
printf '  refused an ambiguous layout instead of guessing which Data directory is production state\n'

# A replaced Data directory must be detected, because that is exactly the accident this whole flow exists to prevent.
replaced_root="$test_root/replaced"
mkdir -p "$replaced_root/Data"
protected_data_identity "$replaced_root/Data"
rm -rf -- "$replaced_root/Data"
# Use a symlink replacement here rather than relying on inode reuse behaviour, which varies by filesystem.
# The production guard must reject any Data replacement, including a path that no longer refers to the original
# protected directory.
ln -s "$replaced_root/recreated-target" "$replaced_root/Data"
if ( assert_data_unchanged ) 2>/dev/null; then
  echo "Expected assert_data_unchanged to reject a replaced Data directory." >&2
  exit 1
fi
printf '  detected a replaced Data directory\n'

# ----------------------------------------------------------------------------------------------------------------------
# Artifact validation
# ----------------------------------------------------------------------------------------------------------------------

good_archive="$test_root/good.tar.gz"
good_tree="$test_root/good-tree"
mkdir -p "$good_tree"
printf 'app\n' > "$good_tree/Adminbot"
printf 'dll\n' > "$good_tree/Adminbot.dll"
tar -czf "$good_archive" -C "$good_tree" .
assert_artifact_archive "$good_archive"

for forbidden in Data/users.db configuration.json credentials.db-log; do
  bad_tree="$test_root/bad-$(echo "$forbidden" | tr '/.' '--')"
  mkdir -p "$bad_tree"
  printf 'app\n' > "$bad_tree/Adminbot"
  printf 'dll\n' > "$bad_tree/Adminbot.dll"
  mkdir -p "$bad_tree/$(dirname -- "$forbidden")"
  printf 'state\n' > "$bad_tree/$forbidden"
  bad_archive="$test_root/bad-$(echo "$forbidden" | tr '/.' '--').tar.gz"
  tar -czf "$bad_archive" -C "$bad_tree" .
  if ( assert_artifact_archive "$bad_archive" ) 2>/dev/null; then
    printf 'Expected the artifact preflight to refuse an archive containing %s.\n' "$forbidden" >&2
    exit 1
  fi
done

mkdir -p "$test_root/Data" && printf 'db\n' > "$test_root/Data/users.db"
tar -czf "$test_root/with-data.tar.gz" -C "$test_root" Data
if ( assert_artifact_archive "$test_root/with-data.tar.gz" ) 2>/dev/null; then
  echo "Expected the artifact preflight to refuse an archive containing a Data directory." >&2
  exit 1
fi

incomplete_tree="$test_root/incomplete-tree"
mkdir -p "$incomplete_tree"
printf 'dll\n' > "$incomplete_tree/Adminbot.dll"
tar -czf "$test_root/incomplete.tar.gz" -C "$incomplete_tree" .
if ( assert_artifact_archive "$test_root/incomplete.tar.gz" ) 2>/dev/null; then
  echo "Expected the artifact preflight to refuse an archive without the Adminbot executable." >&2
  exit 1
fi
printf 'Production artifact preflight test: PASS\n'
printf '  accepts a runtime-only archive\n'
printf '  refuses Data/, database files, and configuration.json\n'
printf '  refuses an archive without the application executable\n'

# A release missing any shipped runtime asset must be refused before it replaces a working one.
complete_release="$test_root/complete-release"
mkdir -p "$complete_release/Assets/telegram-ui"
printf 'app\n' > "$complete_release/Adminbot"
printf 'dll\n' > "$complete_release/Adminbot.dll"
printf '{}\n' > "$complete_release/Assets/telegram-ui/emoji-map.json"
assert_release_complete "$complete_release"
missing_asset_release="$test_root/missing-asset-release"
mkdir -p "$missing_asset_release"
printf 'app\n' > "$missing_asset_release/Adminbot"
printf 'dll\n' > "$missing_asset_release/Adminbot.dll"
if ( assert_release_complete "$missing_asset_release" ) 2>/dev/null; then
  echo "Expected the release completeness check to refuse a release without the premium-UI emoji asset." >&2
  exit 1
fi

# Built-in tutorial asset preflight. The tenant customer flow always offers the three installation tutorials, so a
# release whose images were never copied would ship buttons that always answer with an "unavailable" message. The
# preflight is asserted functionally (it must reject a missing or empty tutorial directory and accept a complete set) and
# structurally (it must run after extraction and before any activation), so it cannot be silently removed later.
asset_stage="$test_root/asset-stage"
mkdir -p "$asset_stage"
for required_dir in android_v2rayng windows_v2rayn ios_android_v2box; do
  mkdir -p "$asset_stage/Assets/tutorials/$required_dir"
  printf 'image\n' > "$asset_stage/Assets/tutorials/$required_dir/1.png"
done
assert_tutorial_assets "$asset_stage"

missing_assets="$test_root/asset-stage-missing"
mkdir -p "$missing_assets/Assets/tutorials/android_v2rayng" "$missing_assets/Assets/tutorials/windows_v2rayn"
printf 'image\n' > "$missing_assets/Assets/tutorials/android_v2rayng/1.png"
printf 'image\n' > "$missing_assets/Assets/tutorials/windows_v2rayn/1.png"
if ( assert_tutorial_assets "$missing_assets" ) 2>/dev/null; then
  echo "Expected the tutorial asset preflight to reject a missing tutorial directory." >&2
  exit 1
fi

empty_assets="$test_root/asset-stage-empty"
mkdir -p "$empty_assets/Assets/tutorials"
for required_dir in android_v2rayng windows_v2rayn ios_android_v2box; do
  mkdir -p "$empty_assets/Assets/tutorials/$required_dir"
done
printf 'metadata\n' > "$empty_assets/Assets/tutorials/windows_v2rayn/Thumbs.db"
if ( assert_tutorial_assets "$empty_assets" ) 2>/dev/null; then
  echo "Expected the tutorial asset preflight to reject a tutorial directory with no images." >&2
  exit 1
fi
printf 'Production tutorial-asset preflight test: PASS\n'
printf '  accepts a release containing all three tutorial directories with images\n'
printf '  rejects a release missing a required tutorial directory\n'
printf '  rejects a tutorial directory that contains no supported image\n'

# ----------------------------------------------------------------------------------------------------------------------
# Structural guarantees
# ----------------------------------------------------------------------------------------------------------------------

# The production host must never build. These assertions are structural because exercising the real path needs a Linux
# host, systemd, and a real transfer, none of which are available to this repository test.
for forbidden_command in 'dotnet restore' 'dotnet build' 'dotnet test' 'dotnet publish' 'dotnet ef' 'dotnet tool' 'git clone'; do
  if grep -Fq -- "$forbidden_command" "$deploy_script"; then
    printf 'Production deployment must not run %s.\n' "$forbidden_command" >&2
    exit 1
  fi
done

for required_command in 'sha256sum' 'assert_artifact_archive' 'assert_data_unchanged' 'assert_tutorial_assets' 'activate_release' 'rollback_release' 'systemctl restart'; do
  if ! grep -Fq -- "$required_command" "$deploy_script"; then
    printf 'Production deployment is missing its %s guard.\n' "$required_command" >&2
    exit 1
  fi
done

digest_line="$(grep -n -m1 -F -- 'Verifying release artifact digest' "$deploy_script" | cut -d: -f1)"
# Every marker below is literal text copied out of deploy-production.sh and matched with -F, so the $name
# fragments must not expand. SC2016 is therefore disabled for exactly those search strings.
# shellcheck disable=SC2016
archive_line="$(grep -n -m1 -F -- 'assert_artifact_archive "$artifact_real"' "$deploy_script" | cut -d: -f1)"
extract_line="$(grep -n -m1 -F -- 'tar -xzf' "$deploy_script" | cut -d: -f1)"
# shellcheck disable=SC2016
complete_line="$(grep -n -m1 -F -- 'assert_release_complete "$stage_publish"' "$deploy_script" | cut -d: -f1)"
# shellcheck disable=SC2016
asset_line="$(grep -n -m1 -F -- 'assert_tutorial_assets "$stage_publish"' "$deploy_script" | cut -d: -f1)"
# shellcheck disable=SC2016
preflight_line="$(grep -n -m1 -F -- 'run_migration_preflight "$stage_publish"' "$deploy_script" | cut -d: -f1)"
# shellcheck disable=SC2016
activate_line="$(grep -n -m1 -F -- 'activate_release "$stage_publish"' "$deploy_script" | cut -d: -f1)"
# shellcheck disable=SC2016
restart_line="$(grep -n -F -- 'restart_and_verify "$service_name"' "$deploy_script" | head -n 1 | cut -d: -f1)"
# shellcheck disable=SC2016
rollback_line="$(grep -n -m1 -F -- 'rollback_release "$live_publish"' "$deploy_script" | cut -d: -f1)"

for required_variable in digest_line archive_line extract_line complete_line asset_line preflight_line activate_line restart_line rollback_line; do
  if [[ -z "${!required_variable}" ]]; then
    printf 'Deployment ordering assertion could not locate %s in deploy-production.sh.\n' "$required_variable" >&2
    exit 1
  fi
done

if ((digest_line >= extract_line || archive_line >= extract_line)); then
  echo "Artifact digest and content checks must run before the archive is extracted." >&2
  exit 1
fi
if ((complete_line >= preflight_line || asset_line >= preflight_line)); then
  echo "Release completeness and tutorial asset checks must run before the migration preflight." >&2
  exit 1
fi
if ((preflight_line >= activate_line)); then
  echo "Migration preflight must run before the release is activated." >&2
  exit 1
fi
if ((activate_line >= restart_line)); then
  echo "The service must only be restarted after the release is activated." >&2
  exit 1
fi
if ((rollback_line <= restart_line)); then
  echo "Rollback must be reachable only after the restart and health check." >&2
  exit 1
fi

printf 'Production release-ordering test: PASS\n'
printf '  artifact digest and content checks run before extraction\n'
printf '  completeness and tutorial checks run before the migration preflight\n'
printf '  migration preflight runs before the atomic switch\n'
printf '  systemd is restarted only after the switch, with rollback after health checks\n'

# Database access has to be proven on the activated release, and the protected state has to be validated before anything
# moves. A release that resolves its database paths at startup keeps running while every worker logs SQLite error 14, so
# "the unit restarted" was never a sufficient health signal.
database_health_line="$(grep -n -m1 -F -- "release_opens_databases \"\$live_publish\"" "$deploy_script" | cut -d: -f1)"
if [[ -z "$database_health_line" || "$database_health_line" -le "$activate_line" ]]; then
  echo "The activated release must prove database access after the switch." >&2
  exit 1
fi
if ! grep -Fq -- "assert_protected_data_ready \"\$live_publish\" \"\$live_data\"" "$deploy_script"; then
  echo "The protected Data directory must be validated before the switch." >&2
  exit 1
fi
printf '  the activated release proves database access, after the protected state was validated before the switch\n'

# The protected state a switch depends on must be refused when it is not usable, because SQLite would otherwise create the
# missing database file and the bot would appear to have lost all of its production state.
ready_live="$test_root/ready/bin/Release/net10.0/linux-x64/publish"
mkdir -p "$ready_live/Data"
printf 'live-database\n' > "$ready_live/Data/users.db"
printf 'live-credentials\n' > "$ready_live/Data/credentials.db"
assert_protected_data_ready "$ready_live" "$ready_live/Data"

rm -f "$ready_live/Data/credentials.db"
if ( assert_protected_data_ready "$ready_live" "$ready_live/Data" ) 2>/dev/null; then
  echo "Expected the deployment to refuse a Data directory without credentials.db." >&2
  exit 1
fi
: > "$ready_live/Data/credentials.db"
if ( assert_protected_data_ready "$ready_live" "$ready_live/Data" ) 2>/dev/null; then
  echo "Expected the deployment to refuse an empty credentials.db." >&2
  exit 1
fi
printf 'live-credentials\n' > "$ready_live/Data/credentials.db"
# Permission bits only constrain a non-root process, so this case is skipped when the suite itself runs as root.
if [[ "$(id -u)" != "0" ]]; then
  chmod 500 "$ready_live/Data"
  if ( assert_protected_data_ready "$ready_live" "$ready_live/Data" ) 2>/dev/null; then
    echo "Expected the deployment to refuse a Data directory the service could not write." >&2
    exit 1
  fi
  chmod 700 "$ready_live/Data"
fi
printf 'Production protected-state validation test: PASS\n'
printf '  refused a missing database file and an empty database file\n'
printf '  refused a Data directory that could not be written\n'

# The database health check must follow what the deployed executable really reports. A stub stands in for the published
# binary so the wiring is verified without building a release.
health_live="$test_root/health/bin/Release/net10.0/linux-x64/publish"
mkdir -p "$health_live/Data"
printf 'live-database\n' > "$health_live/Data/users.db"
printf 'live-credentials\n' > "$health_live/Data/credentials.db"
printf '#!/usr/bin/env bash\nexit 0\n' > "$health_live/Adminbot"
chmod +x "$health_live/Adminbot"
if ! release_opens_databases "$health_live"; then
  echo "Expected the database health check to pass when the release opens the databases." >&2
  exit 1
fi
printf '#!/usr/bin/env bash\nexit 1\n' > "$health_live/Adminbot"
if ( release_opens_databases "$health_live" ) 2>/dev/null; then
  echo "Expected the database health check to fail when the release cannot open the databases." >&2
  exit 1
fi
rm -f "$health_live/Adminbot"
if ( release_opens_databases "$health_live" ) 2>/dev/null; then
  echo "Expected the database health check to fail when the release has no executable." >&2
  exit 1
fi
printf 'Production database health-check test: PASS\n'
printf '  passed on a release that opens the databases and failed on one that cannot\n'

# The deployment lock must be bounded and must be released by the deploy's own teardown. Otherwise one interrupted run
# leaves an orphaned process holding /run/lock/vpnetiran-deploy.lock, and every later release stalls on flock instead of
# failing with a reason - the wedge observed in production.
grep -Fq -- "flock -w \"\$LOCK_WAIT_SECONDS\" -x 9" "$deploy_script" \
  || { echo "The deployment lock must be acquired with a bounded wait." >&2; exit 1; }
for teardown_trap in "trap on_exit EXIT" "trap 'exit 130' INT" "trap 'exit 143' TERM" "trap 'exit 129' HUP"; do
  grep -Fq -- "$teardown_trap" "$deploy_script" \
    || { printf 'Missing deployment teardown trap: %s\n' "$teardown_trap" >&2; exit 1; }
done
for bounded_child in "terminate_preflight_child" "timeout --signal=TERM --kill-after=30"; do
  grep -Fq -- "$bounded_child" "$deploy_script" \
    || { printf 'The deploy must bound and reap its long-running child: %s\n' "$bounded_child" >&2; exit 1; }
done
printf 'Production deployment lock-safety test: PASS\n'
printf '  the deployment lock is acquired with a bounded wait\n'
printf '  EXIT, INT, TERM, and HUP run a teardown that reaps the preflight and releases the lock\n'

if streamed_output="$(bash -s -- invalid-sha "$test_root/good.tar.gz" "$(printf 'a%.0s' {1..64})" 1 1 "$EXPECTED_LIVE_ROOT" "$EXPECTED_SERVICE_NAME" \
  < "$deploy_script" 2>&1)"; then
  echo "Expected streamed deployment entrypoint to reject an invalid SHA." >&2
  exit 1
fi
grep -Fq "deployment SHA must be exactly 40 hexadecimal characters" <<< "$streamed_output"
[[ "$streamed_output" != *"BASH_SOURCE"* ]]
printf '  streamed bash -s entrypoint safely rejected invalid SHA\n'
