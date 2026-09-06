#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
test_root="$(mktemp -d)"
trap 'rm -rf -- "$test_root"' EXIT
install_root="$test_root/install"
old_sha="1111111111111111111111111111111111111111"
bad_sha="2222222222222222222222222222222222222222"
good_sha="3333333333333333333333333333333333333333"
bin_dir="$test_root/bin"
mkdir -p "$install_root/shared/Data" "$install_root/releases/$old_sha" "$install_root/releases/$bad_sha" "$install_root/releases/$good_sha" "$bin_dir"
for state_file in users.db credentials.db telegram-log-outbox.db configuration.json xui-v3-service-plans.json; do
  : > "$install_root/shared/Data/$state_file"
done
ln -s "$install_root/releases/$old_sha" "$install_root/current"

cat > "$bin_dir/systemctl" <<'EOF'
#!/usr/bin/env bash
echo "$*" >> "$SYSTEMCTL_TEST_LOG"
exit 0
EOF
chmod +x "$bin_dir/systemctl"

cat > "$install_root/releases/$bad_sha/Adminbot" <<'EOF'
#!/usr/bin/env bash
exit 1
EOF
cat > "$install_root/releases/$good_sha/Adminbot" <<EOF
#!/usr/bin/env bash
echo "[Build] Commit=$good_sha"
echo "[Build] Configuration=Release"
exit 0
EOF
chmod +x "$install_root/releases/$bad_sha/Adminbot" "$install_root/releases/$good_sha/Adminbot"

export PATH="$bin_dir:$PATH"
export SYSTEMCTL_TEST_LOG="$test_root/systemctl.log"
if bash "$script_dir/activate-release.sh" "$install_root/releases/$bad_sha" "$install_root" test.service "$bad_sha"; then
  echo "Expected failed validator to refuse activation." >&2
  exit 1
fi
[[ "$(readlink -f "$install_root/current")" == "$install_root/releases/$old_sha" ]]
[[ ! -e "$SYSTEMCTL_TEST_LOG" ]]

bash "$script_dir/activate-release.sh" "$install_root/releases/$good_sha" "$install_root" test.service "$good_sha"
[[ "$(readlink -f "$install_root/current")" == "$install_root/releases/$good_sha" ]]
[[ "$(readlink -f "$install_root/previous")" == "$install_root/releases/$old_sha" ]]
grep -Fxq "stop test.service" "$SYSTEMCTL_TEST_LOG"
grep -Fxq "restart test.service" "$SYSTEMCTL_TEST_LOG"
grep -Fxq "is-active --quiet test.service" "$SYSTEMCTL_TEST_LOG"
echo "Deployment activation guard tests: PASS"
