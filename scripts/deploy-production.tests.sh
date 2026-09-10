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
