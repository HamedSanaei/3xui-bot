#!/usr/bin/env bash
set -euo pipefail

repository="${1:?clean repository path is required}"
commit_sha="${2:?exact commit SHA is required}"
install_root="${3:-/opt/vpnetiran}"
service_name="${4:-vpnetiranbot.service}"

if [[ ! "$commit_sha" =~ ^[0-9a-fA-F]{40}$ ]]; then
  echo "Deployment refused: commit SHA must contain exactly 40 hexadecimal characters." >&2
  exit 1
fi
cd "$repository"
if [[ -n "$(git status --porcelain)" ]]; then
  echo "Deployment refused: source tree is dirty." >&2
  exit 1
fi
actual_sha="$(git rev-parse HEAD)"
if [[ "$actual_sha" != "$commit_sha" ]]; then
  echo "Deployment refused: checkout does not match requested commit." >&2
  exit 1
fi
echo "Building commit: $actual_sha"

dotnet tool restore
dotnet restore Adminbot.sln
dotnet build Adminbot.sln -c Release --no-restore "/p:SourceRevisionId=$commit_sha"
dotnet test Adminbot.Tests/Adminbot.Tests.csproj -c Release --no-build
dotnet ef migrations has-pending-model-changes --no-build --project Adminbot.csproj --startup-project Adminbot.csproj --context UserDbContext --configuration Release
dotnet ef migrations has-pending-model-changes --no-build --project Adminbot.csproj --startup-project Adminbot.csproj --context CredentialsDbContext --configuration Release

releases_root="$install_root/releases"
staging_dir="$releases_root/$commit_sha.staging.$$"
release_dir="$releases_root/$commit_sha"
mkdir -p "$releases_root"
if [[ -e "$release_dir" || -e "$staging_dir" ]]; then
  echo "Deployment refused: release or staging directory already exists." >&2
  exit 1
fi
dotnet publish Adminbot.csproj -c Release -f net10.0 -r linux-x64 --self-contained false \
  -o "$staging_dir" "/p:SourceRevisionId=$commit_sha"
"$staging_dir/Adminbot" --migration-check
"$staging_dir/Adminbot" --migration-check \
  --users-source "$install_root/shared/Data/users.db" \
  --credentials-source "$install_root/shared/Data/credentials.db"

ln -s "$install_root/shared/Data" "$staging_dir/Data"
mv "$staging_dir" "$release_dir"
bash "$repository/scripts/activate-release.sh" "$release_dir" "$install_root" "$service_name" "$commit_sha"
