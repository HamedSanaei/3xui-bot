#!/usr/bin/env bash
set -Eeuo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=verify-release.sh
source "$script_dir/verify-release.sh"
test_root="$(mktemp -d)"
trap 'rm -rf -- "$test_root"' EXIT
sha=1111111111111111111111111111111111111111
verifier="$script_dir/release-artifact.py"
export TEST_ROOT="$test_root" TEST_SHA="$sha"
mkdir -p "$test_root/mock-bin" "$test_root/source" "$test_root/fixtures"
real_rsync="$(command -v rsync)"
export REAL_RSYNC="$real_rsync"

# These command fixtures drive the real orchestration/timeout/archive/rsync paths without .NET or production access.
cat > "$test_root/mock-bin/git" <<'SH'
#!/usr/bin/env bash
case "${*: -2}" in
  'rev-parse HEAD') printf '%s\n' "$TEST_SHA" ;;
  *) [[ "${MOCK_DIRTY:-}" != yes ]] || printf ' M tracked.cs\n' ;;
esac
SH
cat > "$test_root/mock-bin/dotnet" <<'SH'
#!/usr/bin/env bash
set -Eeuo pipefail
step="$1"
if [[ "$1" == tool ]]; then step=tool; fi
if [[ "$1" == ef ]]; then
  if [[ "$*" == *UserDbContext* ]]; then step=user-ef; else step=credentials-ef; fi
fi
if [[ "$1" == test && "$*" == *--list-tests* ]]; then step=discovery; fi
printf '%s\n' "$step" >> "$TEST_ROOT/gate.log"
[[ "${MOCK_FAIL:-}" != "$step" ]] || exit 23
if [[ "$step" == discovery ]]; then
  printf 'The following Tests are available:\n'
  [[ "${MOCK_FAIL:-}" == emptytests ]] || printf '    Test.One\n'
elif [[ "$step" == test ]]; then
  while [[ "$#" -gt 0 ]]; do
    if [[ "$1" == --results-directory ]]; then results="$2"; break; fi
    shift
  done
  mkdir -p "$results"
  if [[ "${MOCK_FAIL:-}" == incomplete ]]; then
    cp "$TEST_ROOT/fixtures/incomplete.trx" "$results/release.trx"
  else
    cp "$TEST_ROOT/fixtures/complete.trx" "$results/release.trx"
  fi
elif [[ "$step" == publish ]]; then
  while [[ "$#" -gt 0 ]]; do
    if [[ "$1" == -o ]]; then output="$2"; break; fi
    shift
  done
  mkdir -p "$output"
  cp -r "$TEST_ROOT/fixtures/publish/." "$output/"
  [[ "${MOCK_FAIL:-}" != missingexec ]] || rm "$output/Adminbot"
fi
SH
cat > "$test_root/mock-bin/systemctl" <<'SH'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$TEST_ROOT/service.log"
[[ "${MOCK_SERVICE_FAIL:-}" != yes ]]
SH
cat > "$test_root/mock-bin/rsync" <<'SH'
#!/usr/bin/env bash
printf 'sync\n' >> "$TEST_ROOT/sync.log"
[[ "${MOCK_SYNC_FAIL:-}" != yes ]] || exit 23
exec "$REAL_RSYNC" "$@"
SH
chmod +x "$test_root/mock-bin/"*
export PATH="$test_root/mock-bin:$PATH"

mkdir -p "$test_root/fixtures/publish/Assets/tutorials"
for directory in "${REQUIRED_TUTORIAL_ASSET_DIRS[@]}"; do
  mkdir -p "$test_root/fixtures/publish/Assets/tutorials/$directory"
  printf 'image\n' > "$test_root/fixtures/publish/Assets/tutorials/$directory/1.png"
done
cat > "$test_root/fixtures/publish/Adminbot" <<'SH'
#!/usr/bin/env bash
set -Eeuo pipefail
[[ ! -e /proc/self/fd/9 ]] || exit 31
printf '%s\n' "$*" >> "$TEST_ROOT/migration.log"
[[ "${MOCK_FAIL:-}" != fresh-migration && "${MOCK_LIVE_FAIL:-}" != yes ]] || exit 24
if [[ "$#" -gt 1 ]]; then
  [[ "$1" == --migration-check && "$2" == --users-source && "$4" == --credentials-source ]]
  [[ -f "$3" && -f "$5" ]]
fi
SH
chmod +x "$test_root/fixtures/publish/Adminbot"
for file in Adminbot.dll Adminbot.deps.json Adminbot.runtimeconfig.json; do
  printf 'runtime\n' > "$test_root/fixtures/publish/$file"
done
cat > "$test_root/fixtures/complete.trx" <<'XML'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult testName="Test.One" outcome="Passed" /></Results><ResultSummary outcome="Completed"><Counters total="1" executed="1" passed="1" failed="0" /></ResultSummary></TestRun>
XML
cat > "$test_root/fixtures/incomplete.trx" <<'XML'
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results><UnitTestResult outcome="Passed" /></Results><ResultSummary outcome="Aborted"><Counters total="1" executed="1" passed="1" /></ResultSummary></TestRun>
XML

# Deliberately call under an OR-list, reproducing the Bash errexit suppression behind failed run 37996826248.
for step in tool restore build discovery test incomplete emptytests user-ef credentials-ef publish missingexec fresh-migration; do
  : > "$test_root/gate.log"
  export MOCK_FAIL="$step"
  if ( verify_release "$sha" "$test_root/output-$step" "$test_root/source" ) > "$test_root/failure.log" 2>&1; then
    printf 'Expected release gate refusal at %s\n' "$step" >&2; exit 1
  fi
  [[ ! -e "$test_root/output-$step/release.tar.gz" ]]
  [[ ! -e "$test_root/sync.log" && ! -e "$test_root/service.log" ]]
  if [[ "$step" == test || "$step" == incomplete || "$step" == emptytests ]]; then
    [[ "$(cat "$test_root/gate.log")" != *user-ef* ]]
  fi
done
unset MOCK_FAIL
export MOCK_DIRTY=yes
if ( verify_release "$sha" "$test_root/dirty-output" "$test_root/source" ) >/dev/null 2>&1; then exit 1; fi
unset MOCK_DIRTY
[[ ! -e "$test_root/dirty-output" ]]
verify_release "$sha" "$test_root/approved" "$test_root/source"
archive="$test_root/approved/release.tar.gz"
checksum="$(cat "$test_root/approved/release.sha256")"
[[ "$(cat "$test_root/gate.log")" == *credentials-ef* ]]
printf 'Runner command, completion, publish and fresh-migration gates: PASS\n'

# Real timeout and inherited-lock behavior, not source-text assertions.
if run_bounded 1 bash -c 'sleep 30'; then echo 'Timeout unexpectedly succeeded' >&2; exit 1; fi
exec 9>"$test_root/deploy.lock"
flock -x 9
run_bounded 5 bash -c 'test ! -e /proc/self/fd/9'
if flock -w 1 "$test_root/deploy.lock" true; then echo 'Expected bounded lock refusal' >&2; exit 1; fi
exec 9>&-
printf 'Command timeout and lock-descriptor isolation: PASS\n'

live_publish="$test_root/live/bin/Release/net10.0/linux-x64/publish"
mkdir -p "$live_publish/Data/Telemetry"
printf 'live-db\n' > "$live_publish/Data/users.db"
printf 'live-credentials\n' > "$live_publish/Data/credentials.db"
printf 'live-config\n' > "$live_publish/Data/configuration.json"
printf 'live-token\n' > "$live_publish/token.txt"
printf 'root-config\n' > "$live_publish/configuration.json"
printf 'root-db\n' > "$live_publish/legacy.db"
printf 'wal\n' > "$live_publish/legacy.db-wal"
printf 'live-telemetry\n' > "$live_publish/Data/Telemetry/live.jsonl"
printf 'old\n' > "$live_publish/Adminbot.dll"
printf 'obsolete\n' > "$live_publish/obsolete.dll"
data_identity="$(stat -Lc '%d:%i' "$live_publish/Data")"
assert_data_unchanged() {
  [[ "$(stat -Lc '%d:%i' "$live_publish/Data")" == "$data_identity" ]]
}

# Invalid digest, commit and production-copy migration must never reach synchronization or restart.
for refusal in checksum commit migration; do
  rejected_sha="$sha" rejected_checksum="$checksum"
  [[ "$refusal" != checksum ]] || rejected_checksum="${checksum:1}${checksum:0:1}"
  [[ "$refusal" != commit ]] || rejected_sha=2222222222222222222222222222222222222222
  if [[ "$refusal" == migration ]]; then export MOCK_LIVE_FAIL=yes; fi
  if ( install_verified_release "$archive" "$rejected_sha" "$rejected_checksum" "$test_root/refused-$refusal" \
       "$live_publish" "$EXPECTED_SERVICE_NAME" "$verifier" ) >/dev/null 2>&1; then exit 1; fi
  unset MOCK_LIVE_FAIL
  [[ ! -e "$test_root/sync.log" && ! -e "$test_root/service.log" ]]
  [[ "$(cat "$live_publish/Adminbot.dll")" == old ]]
done

# Forge malicious archives with correctly recomputed outer checksums: structural/content validation must still refuse.
python3 - "$archive" "$test_root" <<'PY'
import hashlib, io, json, pathlib, sys, tarfile
archive, directory = sys.argv[1:]
with tarfile.open(archive) as source:
    originals = [(m, source.extractfile(m).read()) for m in source.getmembers()]
for attack in ('traversal', 'absolute', 'symlink', 'hardlink', 'duplicate', 'tamper', 'data', 'tests', 'configuration', 'token'):
    path = pathlib.Path(directory) / (attack + '.tar.gz')
    with tarfile.open(path, 'w:gz', format=tarfile.USTAR_FORMAT) as target:
        for member, data in originals:
            if attack == 'tamper' and member.name == 'publish/Adminbot.dll':
                data = b'changed-runtime\n'
                member = tarfile.TarInfo(member.name)
                member.size, member.mode = len(data), 0o644
            target.addfile(member, io.BytesIO(data))
        if attack != 'tamper':
            names = {'traversal': '../outside', 'absolute': '/tmp/outside', 'symlink': 'publish/link',
                     'hardlink': 'publish/link', 'duplicate': 'publish/Adminbot.dll',
                     'data': 'publish/Data/users.db', 'tests': 'publish/Adminbot.Tests.dll',
                     'configuration': 'publish/configuration.json', 'token': 'publish/token.txt'}
            item = tarfile.TarInfo(names[attack])
            item.mode = 0o644
            if attack in ('symlink', 'hardlink'):
                item.type = tarfile.SYMTYPE if attack == 'symlink' else tarfile.LNKTYPE
                item.linkname = '/tmp/outside'
                target.addfile(item)
            else:
                data = b'injected'
                item.size = len(data)
                target.addfile(item, io.BytesIO(data))
    path.with_suffix('.sha256').write_text(hashlib.sha256(path.read_bytes()).hexdigest())
PY
for attack in traversal absolute symlink hardlink duplicate tamper data tests configuration token; do
  if ( install_verified_release "$test_root/$attack.tar.gz" "$sha" "$(cat "$test_root/$attack.tar.sha256")" \
       "$test_root/extract-$attack" "$live_publish" "$EXPECTED_SERVICE_NAME" "$verifier" ) >/dev/null 2>&1; then
    printf 'Expected malicious archive refusal: %s\n' "$attack" >&2; exit 1
  fi
  [[ ! -e "$test_root/extract-$attack" && ! -e "$test_root/sync.log" && ! -e "$test_root/service.log" ]]
done
[[ ! -e "$test_root/outside" ]]
printf 'Archive digest, commit, manifest, tampering, traversal and link rejection: PASS\n'

# Also refuse forbidden files when the producer creates a consistent manifest, not just when a member is unlisted.
for forbidden in Data/users.db Adminbot.Tests.dll configuration.json token.txt; do
  candidate="$test_root/producer-${forbidden//\//-}"
  mkdir -p "$candidate"
  cp -r "$test_root/fixtures/publish/." "$candidate/"
  mkdir -p "$(dirname "$candidate/$forbidden")"
  printf 'sensitive\n' > "$candidate/$forbidden"
  if python3 "$verifier" pack "$candidate" "$sha" "$candidate.tar.gz" >/dev/null 2>&1; then exit 1; fi
done

# Tutorial loss after publish and a synchronization failure both block service restart.
cp -r "$test_root/fixtures/publish" "$test_root/missing-tutorial"
rm -rf "$test_root/missing-tutorial/Assets/tutorials/android_v2rayng"
if ( assert_tutorial_assets "$test_root/missing-tutorial" ) >/dev/null 2>&1; then exit 1; fi
export MOCK_SYNC_FAIL=yes
if install_verified_release "$archive" "$sha" "$checksum" "$test_root/sync-failure" \
   "$live_publish" "$EXPECTED_SERVICE_NAME" "$verifier" >/dev/null 2>&1; then exit 1; fi
unset MOCK_SYNC_FAIL
[[ ! -e "$test_root/service.log" ]]
install_verified_release "$archive" "$sha" "$checksum" "$test_root/installed" \
  "$live_publish" "$EXPECTED_SERVICE_NAME" "$verifier"
[[ "$(cat "$live_publish/Adminbot.dll")" == runtime && ! -e "$live_publish/obsolete.dll" ]]
[[ "$(cat "$live_publish/Data/users.db")" == live-db ]]
[[ "$(cat "$live_publish/Data/credentials.db")" == live-credentials ]]
[[ "$(cat "$live_publish/Data/configuration.json")" == live-config ]]
[[ "$(cat "$live_publish/Data/Telemetry/live.jsonl")" == live-telemetry ]]
[[ "$(cat "$live_publish/token.txt")" == live-token ]]
[[ "$(cat "$live_publish/configuration.json")" == root-config ]]
[[ "$(cat "$live_publish/legacy.db")" == root-db && "$(cat "$live_publish/legacy.db-wal")" == wal ]]
[[ "$(cat "$test_root/service.log")" == "restart $EXPECTED_SERVICE_NAME" ]]
[[ "$(cat "$test_root/migration.log")" == *"--users-source $live_publish/Data/users.db --credentials-source $live_publish/Data/credentials.db"* ]]
assert_data_unchanged
printf 'Copy-only preflight, restart blocking and real Data/config/token/database preservation: PASS\n'
