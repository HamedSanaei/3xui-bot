#!/usr/bin/env bash
set -euo pipefail

release_dir="${1:?release directory is required}"
install_root="${2:-/opt/vpnetiran}"
service_name="${3:-vpnetiranbot.service}"
expected_sha="${4:?expected 40-character commit SHA is required}"
shared_data="$install_root/shared/Data"
current_link="$install_root/current"
previous_link="$install_root/previous"
candidate_link="$install_root/.current-candidate-$$"

if [[ ! -x "$release_dir/Adminbot" ]]; then
  echo "Release validation failed: published Adminbot executable is missing." >&2
  exit 1
fi
if [[ ! "$expected_sha" =~ ^[0-9a-fA-F]{40}$ || "$(basename "$release_dir")" != "$expected_sha" ]]; then
  echo "Release validation failed: immutable directory does not match the expected commit." >&2
  exit 1
fi
for state_file in users.db credentials.db telegram-log-outbox.db configuration.json xui-v3-service-plans.json; do
  if [[ ! -e "$shared_data/$state_file" ]]; then
    echo "Release validation failed: required shared state is missing." >&2
    exit 1
  fi
done

preflight_output="$("$release_dir/Adminbot" --migration-check \
  --users-source "$shared_data/users.db" \
  --credentials-source "$shared_data/credentials.db")"
printf '%s\n' "$preflight_output"
grep -Fqx "[Build] Commit=$expected_sha" <<< "$preflight_output"
grep -Fqx "[Build] Configuration=Release" <<< "$preflight_output"

if [[ ! -L "$current_link" ]]; then
  echo "Release validation failed: no current validated release is available for rollback." >&2
  exit 1
fi
old_release="$(readlink -f "$current_link")"
if [[ ! -d "$old_release" ]]; then
  echo "Release validation failed: current release target is unavailable." >&2
  exit 1
fi

ln -s "$release_dir" "$candidate_link"
ln -sfn "$old_release" "$previous_link"

systemctl stop "$service_name"
mv -Tf "$candidate_link" "$current_link"
if systemctl restart "$service_name" && systemctl is-active --quiet "$service_name"; then
  echo "Activated validated release: $(basename "$release_dir")"
  exit 0
fi

rollback_link="$install_root/.current-rollback-$$"
ln -s "$old_release" "$rollback_link"
mv -Tf "$rollback_link" "$current_link"
systemctl restart "$service_name"
echo "New release failed health verification; previous release was restored." >&2
exit 1
