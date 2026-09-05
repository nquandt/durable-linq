# Contributing

## Repository layout

```
DurableLinq.slnx             solution (slnx format)
Directory.Build.props        net10.0, nullable, implicit usings, central package versions
Directory.Packages.props     all package versions live here
src/                         packages, one folder each
tests/                       xUnit tests; store packages run the shared contract suite in Testcontainers
examples/                    runnable examples, simplest first
docs/                        this site
CLAUDE.md                    working brief for contributors and agents
```

## Build and test

```
dotnet build
dotnet test                                    # core + hosting tests need nothing
dotnet test tests/DurableLinq.Redis.Tests      # needs Docker
```

## Store test projects

Every store test project derives from `DurableLinq.Tests.Contract.WorkflowStoreContractTests` and
implements `CreateStoreAsync`. Store test projects use Testcontainers (`Testcontainers.Redis`,
`Testcontainers.PostgreSql`, `Testcontainers.MsSql`) with a shared xUnit collection fixture so one
container serves the whole class. All contract tests must pass for a store implementation to ship. See
[Store interface](architecture/store-interface.md) for the invariants a new store must hold.

## Releases and versioning

Package versions come from git tags, computed by MinVer at build time — there is no version to edit by
hand. See [Release process](release-process.md) for exactly how a merge to `main` becomes a preview
package on GitHub Packages, and how a `vX.Y.Z` tag becomes a release on nuget.org.

## Conventions

- .NET 10, C# latest, nullable on, `ConfigureAwait(false)` in library code.
- No new dependencies beyond `StackExchange.Redis`, `Dapper`, `Npgsql`, `Microsoft.Data.SqlClient`, and
  the `Microsoft.Extensions.*` abstractions already in use. The core package stays dependency free.
- Run `dotnet format` before committing.
- Add every new project to the solution with `dotnet sln add`.

## Building this site locally

```
pip install mkdocs-material
mkdocs serve
```

Then open `http://127.0.0.1:8000`. Pushing to `main` builds and publishes the site to GitHub Pages
through `.github/workflows/docs.yml`.
