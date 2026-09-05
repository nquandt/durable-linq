# Contributing

Thanks for looking at DurableLinq. This file covers the mechanics of contributing; `CLAUDE.md` has the
detailed working conventions (layout, formatting, workflow-authoring rules) that apply to every change.

## Set up

- .NET 10 SDK.
- Docker Desktop (or another Docker daemon) if you plan to run the Redis, Postgres, or SQL Server store
  tests — they use Testcontainers to start real, disposable containers.

```
dotnet build
dotnet test                                    # core + hosting tests need nothing
dotnet test tests/DurableLinq.Redis.Tests      # needs Docker
```

## Before you open a pull request

1. Run `dotnet build` and `dotnet test` for the whole solution.
2. Run `dotnet format` (CI checks `dotnet format --verify-no-changes`).
3. If you added a new project, add it to `DurableLinq.slnx` with `dotnet sln add`.
4. If you touched a store implementation, make sure it still passes the shared
   `DurableLinq.Tests.Contract.WorkflowStoreContractTests` suite — that suite is what every store is held
   to, not just its own tests.
5. If you changed anything user-facing (an API shape, a store's behavior, a package's dependencies),
   update the relevant page under `docs/` and add an entry to `CHANGELOG.md` under "Unreleased".

## What CI runs

`.github/workflows/ci.yml` runs on every push to `main` and every pull request: restore, build, test
(including the Testcontainers-backed store suites, since GitHub-hosted `ubuntu-latest` runners have
Docker), and `dotnet format --verify-no-changes`. A pull request must pass this before merge.

## Versioning and releases

Package versions come from git tags, not from a hand-edited version number in any project file. See
[docs/release-process.md](docs/release-process.md) for exactly how a merge to `main` becomes a preview
package and a tag becomes a release. In short:

- Do not edit `<Version>` anywhere — there isn't one to edit; MinVer computes it from tags.
- If your change is user-facing, add a line to `CHANGELOG.md` under "Unreleased" describing it. The
  person cutting the release moves that section under the new version heading when they tag.

## Reporting issues

Open a GitHub issue with a minimal repro. For a bug in a specific store, include which store, whether it
reproduces against the in-memory store too, and the contract test (if any) it violates.
