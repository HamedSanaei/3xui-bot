# Immutable production deployment

## Incident finding

The outage could not be reproduced from either known clean commit. At the start of this repair,
`8ef69f87281b237998cf596741df8eec56872eb7` passed the EF 10.0.8 pending-model check for both contexts, and its diff
from known-good `1e19198f0e019d3395bb9425b0c5326742cc4e74` contains no entity, property, index, constraint, migration, or model
snapshot change. Consequently there is no honest schema member to name as the mismatch from committed source. The
available evidence identifies the old production checkout's hybrid/dirty source composition as the root deployment
integrity failure: the compiled current model and copied migration/snapshot files did not come from one provable commit.
The guards below reject both real model drift and that hybrid-source condition.

No production artifact may be activated unless its source commit is known, its source tree is clean, the complete
test suite passes, both EF contexts have no pending model changes, real historical migrations succeed, and the exact
published executable passes migration preflight against fresh databases and online-backup copies of production-shaped
databases.

Release artifacts live under `/opt/vpnetiran/releases/<commit-sha>/`. Persistent state lives under
`/opt/vpnetiran/shared/Data/`, and every release's `Data` entry is a symlink to that directory. Persistent state includes
`users.db`, `credentials.db`, `telegram-log-outbox.db`, `configuration.json`, production certificates, and the runtime
`xui-v3-service-plans.json`. These files are excluded from publish and must never be populated or overwritten from its
output. Compiled binaries, runtime libraries, and static application assets are immutable release artifacts.

Prepare the service once with `WorkingDirectory=/opt/vpnetiran/current` and
`ExecStart=/opt/vpnetiran/current/Adminbot`. Use `Restart=on-failure`, `RestartSec=10`, `StartLimitIntervalSec=300`, and
`StartLimitBurst=3` so a programming error cannot restart indefinitely. Keep `/opt/vpnetiran/previous` and do not delete
the prior known-good release during activation. Before using the automated deploy script for the first time, bootstrap
`current` to the already running known-good release; activation refuses to stop systemd without a valid rollback target.

From a clean clone at the requested SHA, run:

```bash
bash scripts/deploy-release.sh /path/to/clean/clone <40-character-commit-sha> /opt/vpnetiran vpnetiranbot.service
```

The script refuses a dirty or mismatched checkout, restores the pinned EF tool, builds, runs all tests and both pending
model checks, publishes into a new staging directory, runs the published executable's fresh and production-copy
preflights, then creates the immutable release. Only after every validation succeeds does it stop the healthy service,
atomically switch `current`, and restart. A failed preflight never calls systemd or changes `current`; a failed health
check restores `current` to the previous validated release and restarts it.

The application logs `[Build] Commit=<sha>` and `[Build] Configuration=Release` from assembly metadata. The migration
preflight starts no web server, Telegram receiver, hosted worker, or remote logger:

```bash
/opt/vpnetiran/releases/<sha>/Adminbot --migration-check \
  --users-source /opt/vpnetiran/shared/Data/users.db \
  --credentials-source /opt/vpnetiran/shared/Data/credentials.db
```

## Artifact deployment gate (`scripts/deploy-production.sh`)

The GitHub Actions pipeline builds once on the runner and deploys exactly that artifact. Production never clones the
repository and never runs `dotnet restore`, `dotnet build`, `dotnet test`, `dotnet publish`, or `dotnet ef`.

`.github/workflows/ci.yml` is the single build pipeline. It runs on push and pull requests, and it is also callable as a
reusable workflow. It performs, in order:

```bash
dotnet tool restore
dotnet restore Adminbot.sln
dotnet build Adminbot.sln -c Release --no-restore "/p:SourceRevisionId=<sha>"
dotnet test Adminbot.Tests/Adminbot.Tests.csproj -c Release --no-build
dotnet ef migrations has-pending-model-changes --no-build --context UserDbContext
dotnet ef migrations has-pending-model-changes --no-build --context CredentialsDbContext
bash -n scripts/deploy-production.sh
bash -n scripts/deploy-production.tests.sh
bash scripts/deploy-production.tests.sh
shellcheck scripts/deploy-production.sh scripts/deploy-production.tests.sh
dotnet publish Adminbot.csproj -c Release -f net10.0 -r linux-x64 --self-contained false -o <tmp>
```

`shellcheck` runs at its default severity, so warnings and info-level findings fail the pipeline as well. Both scripts
are kept lint-clean; the structural assertions in `scripts/deploy-production.tests.sh` single-quote `$name` markers on
purpose and disable `SC2016` for exactly those search strings.

It then refuses to package anything that is not a runtime-only release: `Data/`, `*.db`, `*.db-*`, and
`configuration.json` are rejected, while `Adminbot`, `Adminbot.dll`, `Assets/telegram-ui/emoji-map.json`, and all three
`Assets/tutorials/*` image directories are required. The packaged `vpnetiranbot-release` artifact (tarball plus
`.sha256`, with the digest exposed as a job output) is the only thing that may reach production.

`.github/workflows/deploy-production.yml` calls that workflow as job `ci`, downloads its artifact, verifies the digest,
copies it to `/root/.deploy/incoming/<sha>/`, and streams `scripts/deploy-production.sh` to the host with the SHA,
artifact path, digest, run id/attempt, live root, and service name. The host then, in order:

1. verifies tooling, the .NET 10 runtime, and that the service's `ExecStart` still points at the expected live executable;
2. captures the protected `Data` directory identity (realpath plus device:inode) and refuses to continue unless that
   directory really holds both production databases, non-empty and writable, inside a writable release parent;
3. verifies the transferred archive's SHA-256 against the runner's digest;
4. verifies the archive's entry list contains no `Data/`, database, configuration, or `.git` entry, and does contain the
   executable and its assembly;
5. extracts into a unique staging directory, re-checks for `Data/`, requires the runtime assets, and runs the
   tutorial-asset preflight;
6. runs the published executable's `--migration-check` against fresh databases and then against online-backup copies of
   `Data/users.db` and `Data/credentials.db`;
7. activates the release with a sequence of same-filesystem renames (the retained rollback slot is emptied and its
   occupant parked, live -> `publish.prev`, staged -> `publish`, then `publish.prev/Data` -> `publish/Data`), so the
   protected `Data` directory keeps its inode and no database, `configuration.json`, or log file is ever copied, replaced,
   or truncated; the parked release is discarded only after the Data move succeeded, and a release left without Data by an
   interrupted switch is repaired from the retained slot when exactly one plausible candidate exists;
8. restarts the service, waits for it to become active, and then proves the activated release can open the production
   databases with the published executable's read-only `--migration-check`. Both the restart/health step and this database
   check are required before the deployment is declared successful, and either failure rolls back to the retained previous
   release (Data renamed back, service restarted again) - a running unit alone is not evidence that the application can
   reach its databases.

`assert_data_unchanged` re-checks the `Data` identity before and after every step that touches the filesystem, and
`activate_release` refuses to run when staging and the live publish directory are on different filesystems, because the
renames would then silently degrade into copies.

`scripts/deploy-production.tests.sh` proves the switch and the rollback functionally against temporary directories
(Data content **and inode** preserved across both), proves a recreated `Data` directory is detected, proves the artifact
preflight refuses archives carrying protected state and accepts a runtime-only archive, and asserts structurally that the
script contains no `dotnet restore/build/test/publish/ef/tool`, no `git clone`, and that digest checks precede
extraction, completeness and tutorial checks precede the preflight, the preflight precedes activation, activation precedes
the restart, and rollback is reachable only after the restart and health check.

## Test schema policy

The general concurrency fixture in `Adminbot.Tests/ConcurrencyTests.cs` uses `EnsureCreated` only for short-lived unit
and concurrency databases that need the current schema and do not claim migration coverage. Every migration,
startup-compatibility, deployment, historical-upgrade, and artifact-preflight test uses `Database.Migrate` and the real
migration assembly. No migration guard uses `EnsureCreated` or `EnsureCreatedAsync`.
