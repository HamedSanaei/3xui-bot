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

# A replaced Data directory must be detected, because that is exactly the accident this whole flow exists to prevent.
replaced_root="$test_root/replaced"
mkdir -p "$replaced_root/Data"
protected_data_identity "$replaced_root/Data"
rm -rf -- "$replaced_root/Data"
mkdir -p "$replaced_root/Data"
if ( assert_data_unchanged ) 2>/dev/null; then
  echo "Expected assert_data_unchanged to reject a recreated Data directory." >&2
  exit 1
fi
printf '  detected a recreated Data directory\n'

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
archive_line="$(grep -n -m1 -F -- 'assert_artifact_archive "$artifact_real"' "$deploy_script" | cut -d: -f1)"
extract_line="$(grep -n -m1 -F -- 'tar -xzf' "$deploy_script" | cut -d: -f1)"
complete_line="$(grep -n -m1 -F -- 'assert_release_complete "$stage_publish"' "$deploy_script" | cut -d: -f1)"
# Search for the literal expression; $stage_publish must not expand in this structural assertion.
# shellcheck disable=SC2016
asset_line="$(grep -n -m1 -F -- 'assert_tutorial_assets "$stage_publish"' "$deploy_script" | cut -d: -f1)"
preflight_line="$(grep -n -m1 -F -- 'run_migration_preflight "$stage_publish"' "$deploy_script" | cut -d: -f1)"
activate_line="$(grep -n -m1 -F -- 'activate_release "$stage_publish"' "$deploy_script" | cut -d: -f1)"
restart_line="$(grep -n -F -- 'restart_and_verify "$service_name"' "$deploy_script" | head -n 1 | cut -d: -f1)"
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

if streamed_output="$(bash -s -- invalid-sha "$test_root/good.tar.gz" "$(printf 'a%.0s' {1..64})" 1 1 "$EXPECTED_LIVE_ROOT" "$EXPECTED_SERVICE_NAME" \
  < "$deploy_script" 2>&1)"; then
  echo "Expected streamed deployment entrypoint to reject an invalid SHA." >&2
  exit 1
fi
grep -Fq "deployment SHA must be exactly 40 hexadecimal characters" <<< "$streamed_output"
[[ "$streamed_output" != *"BASH_SOURCE"* ]]
printf '  streamed bash -s entrypoint safely rejected invalid SHA\n'
