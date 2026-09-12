#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=deploy-production.sh
# shellcheck disable=SC1091
source "$script_dir/deploy-production.sh"

test_root="$(mktemp -d)"
outside_marker="$(mktemp)"
cleanup_test() {
  rm -rf -- "$test_root"
  rm -f -- "$outside_marker"
}
trap cleanup_test EXIT

live_root="$test_root/live"
live_publish="$live_root/bin/Release/net10.0/linux-x64/publish"
stage_publish="$test_root/stage/publish"
stage_source="$test_root/stage/source"
mkdir -p "$live_publish/Data" "$stage_publish/Data" "$stage_source"

printf 'persistent-production-state\n' > "$live_publish/Data/preserve.txt"
printf 'outside-test-sentinel\n' > "$outside_marker"
printf 'old-app\n' > "$live_publish/app.dll"
printf 'obsolete-app\n' > "$live_publish/obsolete.dll"
printf 'new-app\n' > "$stage_publish/app.dll"
printf 'new-library\n' > "$stage_publish/new-library.dll"
printf 'must-never-enter-live-data\n' > "$stage_publish/Data/injected.txt"

sync_publish "$stage_publish" "$live_publish"

[[ "$(cat "$live_publish/Data/preserve.txt")" == "persistent-production-state" ]]
[[ ! -e "$live_publish/Data/injected.txt" ]]
[[ "$(cat "$live_publish/app.dll")" == "new-app" ]]
[[ -f "$live_publish/new-library.dll" ]]
[[ ! -e "$live_publish/obsolete.dll" ]]

# A release may never replace, delete, or overwrite the live SQLite databases. The exclusions below are the only
# thing that stops publish/source synchronization from touching them, so they are asserted structurally: removing
# one of them must fail this test instead of silently shipping a release that can wipe production data.
grep -Fq -- "--exclude='*.db'" "$script_dir/deploy-production.sh" \
  || { echo 'Missing *.db synchronization exclusion in deploy-production.sh.' >&2; exit 1; }
grep -Fq -- "--exclude='*.db-*'" "$script_dir/deploy-production.sh" \
  || { echo 'Missing *.db-* synchronization exclusion in deploy-production.sh.' >&2; exit 1; }
grep -Fq -- "--exclude='Data/'" "$script_dir/deploy-production.sh" \
  || { echo 'Missing Data/ synchronization exclusion in deploy-production.sh.' >&2; exit 1; }
grep -Fq 'assert_data_unchanged()' "$script_dir/deploy-production.sh" \
  || { echo 'Missing assert_data_unchanged guard in deploy-production.sh.' >&2; exit 1; }

# A live users.db must survive a synchronization that carries a staged database file with the same name.
printf 'live-database\n' > "$live_publish/Data/users.db"
printf 'staged-database\n' > "$stage_publish/Data/users.db"
sync_publish "$stage_publish" "$live_publish"
[[ "$(cat "$live_publish/Data/users.db")" == "live-database" ]]
printf '  protected live users.db from staged replacement\n'

printf 'tracked-source\n' > "$stage_source/README.deploy-test"
printf 'stale-source\n' > "$live_root/stale-source.txt"
sync_source "$stage_source" "$live_root"

[[ -f "$live_root/README.deploy-test" ]]
[[ ! -e "$live_root/stale-source.txt" ]]
[[ "$(cat "$live_publish/Data/preserve.txt")" == "persistent-production-state" ]]
[[ "$(cat "$outside_marker")" == "outside-test-sentinel" ]]

printf 'Production Data preservation test: PASS\n'
printf '  preserved Data/preserve.txt content\n'
printf '  rejected staged Data injection\n'
printf '  removed stale non-Data publish/source files\n'
printf '  installed new publish/source files\n'
printf '  left outside-of-test marker unchanged\n'

if streamed_output="$(bash -s -- invalid-sha "$CANONICAL_REPO_URL" 1 1 "$EXPECTED_LIVE_ROOT" "$EXPECTED_SERVICE_NAME" \
  < "$script_dir/deploy-production.sh" 2>&1)"; then
  echo "Expected streamed deployment entrypoint to reject an invalid SHA." >&2
  exit 1
fi
grep -Fq "deployment SHA must be exactly 40 hexadecimal characters" <<< "$streamed_output"
[[ "$streamed_output" != *"BASH_SOURCE"* ]]
printf '  streamed bash -s entrypoint safely rejected invalid SHA\n'

# Release-gate ordering assertions. The production synchronizer must not rely on `dotnet publish` alone: the pinned EF
# tool, restore, build, the full test suite, and both EF pending-model checks have to run, and every one of them - plus
# the published executable's migration preflight - has to run before any source or publish synchronization and before
# systemd is touched. This is asserted structurally because exercising the real path needs a Linux host, rsync, and
# systemd, none of which are available to this repository test.
deploy_script="$script_dir/deploy-production.sh"

first_gate_line="$(grep -n -m1 -F -- 'dotnet tool restore' "$deploy_script" | cut -d: -f1)"
test_line="$(grep -n -m1 -F -- 'dotnet test Adminbot.Tests/Adminbot.Tests.csproj -c Release --no-build' "$deploy_script" | cut -d: -f1)"
user_context_line="$(grep -n -m1 -F -- '--context UserDbContext' "$deploy_script" | cut -d: -f1)"
credentials_context_line="$(grep -n -m1 -F -- '--context CredentialsDbContext' "$deploy_script" | cut -d: -f1)"
publish_line="$(grep -n -m1 -F -- 'dotnet publish Adminbot.csproj' "$deploy_script" | cut -d: -f1)"
last_preflight_line="$(grep -n -F -- '--migration-check' "$deploy_script" | tail -n 1 | cut -d: -f1)"
# Search for the literal deploy-source expression; $stage_source must not expand in this structural assertion.
# shellcheck disable=SC2016
first_sync_line="$(grep -n -m1 -F -- 'sync_source "$stage_source"' "$deploy_script" | cut -d: -f1)"
restart_line="$(grep -n -m1 -F -- 'systemctl restart' "$deploy_script" | cut -d: -f1)"

for required_variable in first_gate_line test_line user_context_line credentials_context_line publish_line last_preflight_line first_sync_line restart_line; do
  if [[ -z "${!required_variable}" ]]; then
    printf 'Release-gate assertion could not locate %s in deploy-production.sh.\n' "$required_variable" >&2
    exit 1
  fi
done

if ((first_gate_line >= publish_line)); then
  echo "Release gates must run before the publish step." >&2
  exit 1
fi
if ((test_line >= first_sync_line || user_context_line >= first_sync_line || credentials_context_line >= first_sync_line)); then
  echo "Tests and both EF pending-model checks must run before the source synchronization." >&2
  exit 1
fi
if ((publish_line >= last_preflight_line || last_preflight_line >= first_sync_line)); then
  echo "Migration preflight must run after publish and before the source synchronization." >&2
  exit 1
fi
if ((first_sync_line >= restart_line)); then
  echo "Synchronization must complete before systemd is restarted." >&2
  exit 1
fi

printf 'Production release-gate ordering test: PASS\n'
printf '  restore, build, tests, and both EF pending-model checks run before any synchronization\n'
printf '  migration preflight runs on the published executable before any synchronization\n'
printf '  systemd is only restarted after the synchronized release passes every gate\n'

# Built-in tutorial asset preflight. The tenant customer flow always offers the three installation tutorials, so a
# release whose images were never copied would ship buttons that always answer with an "unavailable" message. The
# preflight is asserted functionally (it must reject a missing or empty tutorial directory and accept a complete set) and
# structurally (it must run after publish and before any synchronization), so it cannot be silently removed later.
asset_stage="$test_root/asset-stage"
mkdir -p "$asset_stage"
for required_dir in android_v2rayng windows_v2rayn ios_android_v2box; do
  mkdir -p "$asset_stage/Assets/tutorials/$required_dir"
  printf 'image\n' > "$asset_stage/Assets/tutorials/$required_dir/1.png"
done
assert_tutorial_assets "$asset_stage"

# A publish artifact that is missing one entire tutorial directory must be refused.
missing_assets="$test_root/asset-stage-missing"
mkdir -p "$missing_assets/Assets/tutorials/android_v2rayng" "$missing_assets/Assets/tutorials/windows_v2rayn"
printf 'image\n' > "$missing_assets/Assets/tutorials/android_v2rayng/1.png"
printf 'image\n' > "$missing_assets/Assets/tutorials/windows_v2rayn/1.png"
if ( assert_tutorial_assets "$missing_assets" ) 2>/dev/null; then
  echo "Expected the tutorial asset preflight to reject a missing tutorial directory." >&2
  exit 1
fi

# Present but image-less tutorial directory must be refused.
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

# Search for the literal preflight expression; $stage_publish must not expand in this structural assertion.
# shellcheck disable=SC2016
asset_line="$(grep -n -m1 -F -- 'assert_tutorial_assets "$stage_publish"' "$deploy_script" | cut -d: -f1)"
if [[ -z "$asset_line" ]]; then
  echo "The tutorial asset preflight is missing from deploy-production.sh." >&2
  exit 1
fi
if ((asset_line <= publish_line)); then
  echo "The tutorial asset preflight must run after the publish step." >&2
  exit 1
fi
if ((asset_line >= first_sync_line || asset_line >= restart_line)); then
  echo "The tutorial asset preflight must run before any synchronization or systemd restart." >&2
  exit 1
fi

printf 'Production tutorial-asset preflight test: PASS\n'
printf '  accepts a publish artifact containing all three tutorial directories with images\n'
printf '  rejects a publish artifact missing a required tutorial directory\n'
printf '  rejects a tutorial directory that contains no supported image\n'
printf '  runs after publish and before any synchronization or restart\n'
