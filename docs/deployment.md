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

## Synchronized-deployment gate (`scripts/deploy-production.sh`)

The GitHub production workflow streams `scripts/deploy-production.sh` to the host, which clones the exact pushed SHA and
synchronizes it into `/root/vpnetiran` before restarting `vpnetiranbot.service`. That path must clear the same gates as
the immutable-release path; `dotnet publish` alone is explicitly **not** sufficient, because it compiles the application
but never runs the suite or the EF model checks.

Against the freshly cloned staging checkout, and before any source or publish synchronization and before systemd is
touched, the script now runs, in order:

```bash
dotnet tool restore
dotnet restore Adminbot.sln
dotnet build Adminbot.sln -c Release --no-restore "/p:SourceRevisionId=<sha>"
dotnet test Adminbot.Tests/Adminbot.Tests.csproj -c Release --no-build
dotnet ef migrations has-pending-model-changes --no-build --context UserDbContext
dotnet ef migrations has-pending-model-changes --no-build --context CredentialsDbContext
```

It then publishes with the same `SourceRevisionId` stamp, and runs the published executable's migration preflight twice:
once against fresh databases, then against online-backup copies of `.../publish/Data/users.db` and
`.../publish/Data/credentials.db`. Only after every gate passes does it synchronize source and publish and restart the
service; a failing gate exits before the protected `Data` directory or systemd is touched.

`scripts/deploy-production.tests.sh` asserts the *ordering* structurally (restore/build/tests/EF checks and the
preflight all precede synchronization, and synchronization precedes the restart) so the gate cannot be dropped or moved
by a later edit on a machine without rsync or systemd.

## Test schema policy

The general concurrency fixture in `Adminbot.Tests/ConcurrencyTests.cs` uses `EnsureCreated` only for short-lived unit
and concurrency databases that need the current schema and do not claim migration coverage. Every migration,
startup-compatibility, deployment, historical-upgrade, and artifact-preflight test uses `Database.Migrate` and the real
migration assembly. No migration guard uses `EnsureCreated` or `EnsureCreatedAsync`.
