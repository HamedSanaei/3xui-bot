#!/usr/bin/env bash
set -Eeuo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=deploy-production.sh
source "$script_dir/deploy-production.sh"

# Full verification only: no secrets, production paths, SSH, rsync or systemd. Output must be fresh and outside source.
verify_release() {
  local sha="$1" output="$2" source_root="$3"
  local actual_sha canonical_output source_status
  [[ "$sha" =~ ^[0-9a-f]{40}$ ]] || fail "verification requires an exact lowercase commit SHA."
  actual_sha="$(run_bounded 15 git -C "$source_root" rev-parse HEAD)" || return $?
  [[ "$actual_sha" == "$sha" ]] || fail "checkout does not match requested commit."
  source_status="$(run_bounded 15 git -C "$source_root" status --porcelain --untracked-files=all)" || return $?
  [[ -z "$source_status" ]] || fail "release checkout must be clean."
  canonical_output="$(realpath -m -- "$output")" || return $?
  [[ "$canonical_output" != "$source_root" && "$canonical_output" != "$source_root/"* ]] || fail "output must be outside the source checkout."
  [[ ! -e "$canonical_output" && ! -L "$canonical_output" ]] || fail "release output directory must be absent."
  mkdir -p -m 700 -- "$canonical_output" || return $?
  export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 LC_ALL=C DOTNET_CLI_UI_LANGUAGE=en-US
  (
    cd "$source_root" || exit $?
    # Preserve 45fc9c5's explicit failure propagation: an enclosing OR-list disables inherited errexit.
    run_bounded 180 dotnet tool restore || exit $?
    run_bounded 300 dotnet restore Adminbot.sln || exit $?
    run_bounded 900 dotnet build Adminbot.sln -c Release --no-restore "/p:SourceRevisionId=$sha" || exit $?
    run_bounded 180 dotnet test Adminbot.Tests/Adminbot.Tests.csproj -c Release --no-build --list-tests \
      > "$canonical_output/discovered-tests.txt" || exit $?
    run_bounded 1800 dotnet test Adminbot.Tests/Adminbot.Tests.csproj -c Release --no-build \
      --logger 'trx;LogFileName=release.trx' --results-directory "$canonical_output/test-results" \
      --blame-hang-timeout 5m || exit $?
    run_bounded 30 python3 "$script_dir/release-artifact.py" tests \
      "$canonical_output/discovered-tests.txt" "$canonical_output/test-results" || exit $?
    run_bounded 180 dotnet ef migrations has-pending-model-changes --no-build --project Adminbot.csproj \
      --startup-project Adminbot.csproj --context UserDbContext --configuration Release || exit $?
    run_bounded 180 dotnet ef migrations has-pending-model-changes --no-build --project Adminbot.csproj \
      --startup-project Adminbot.csproj --context CredentialsDbContext --configuration Release || exit $?
    # Publish is a gate too, even if called from an OR-list. Only the application enters the archive.
    run_bounded 900 dotnet publish Adminbot.csproj -c Release -f net10.0 -r linux-x64 --self-contained false \
      "/p:SourceRevisionId=$sha" -o "$canonical_output/publish" || exit $?
    assert_tutorial_assets "$canonical_output/publish" || exit $?
    run_bounded 180 "$canonical_output/publish/Adminbot" --migration-check || exit $?
    run_bounded 120 python3 "$script_dir/release-artifact.py" pack "$canonical_output/publish" "$sha" \
      "$canonical_output/release.tar.gz" > "$canonical_output/release.sha256" || exit $?
    local checksum
    checksum="$(cat "$canonical_output/release.sha256")" || exit $?
    run_bounded 120 python3 "$script_dir/release-artifact.py" verify "$canonical_output/release.tar.gz" "$sha" "$checksum" || exit $?
  ) || fail "runner release gate failed; no deployable artifact was approved."
  printf 'Complete runner release gate passed for %s.\n' "$sha"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
  [[ "$#" == 2 ]] || fail "usage: verify-release.sh COMMIT_SHA FRESH_EXTERNAL_OUTPUT_DIRECTORY"
  verify_release "$1" "$2" "$(cd "$script_dir/.." && pwd)"
fi
