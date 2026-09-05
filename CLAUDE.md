# CLAUDE.md — working brief for this repository

Read `OVERVIEW.md` and `DESIGN.md` at the repo root first. This file adds the rules that
are not in those documents.

## Repository layout

```
DurableLinq.slnx             solution (slnx format)
Directory.Build.props        net10.0, nullable, implicit usings, central package versions
Directory.Packages.props     ALL package versions live here. Never put Version= on a PackageReference.
src/
  DurableLinq/               core, zero dependencies (Durable<T>, primitives, runner, IWorkflowStore, InMemory + File stores)
  DurableLinq.Hosting/       AddDurable(...), DurableBuilder, RecoverySweeper, logging + metrics
  DurableLinq.Redis/         (to build) RedisWorkflowStore + UseRedis(...)
  DurableLinq.Postgres/      (to build) PostgresWorkflowStore + UsePostgres(...) + schema script
  DurableLinq.SqlServer/     (to build) SqlServerWorkflowStore + UseSqlServer(...) + schema script
  DurableLinq.Dashboard/     (to build) MapDurableDashboard(...) minimal API + embedded single-page UI
  DurableLinq.Dashboard.Host/(to build) standalone web host + Dockerfile that connects to any store via env vars
  DurableLinq.Analyzers/     Roslyn analyzer (netstandard2.0): DL0001 catch-swallows-step-failure, DL0002 non-durable await.
                             Referenced as an analyzer by the examples and DurableLinq.Tests. Only project allowed
                             to depend on Microsoft.CodeAnalysis; that dependency never reaches a runtime package.
tests/
  DurableLinq.Tests/         core + hosting tests, and Contract/WorkflowStoreContractTests.cs
  DurableLinq.<Store>.Tests/ derive from WorkflowStoreContractTests, use Testcontainers
  DurableLinq.Analyzers.Tests/ compiles snippets against DurableLinq.dll and asserts the analyzer diagnostics
examples/
  ...                        one folder per example, each its own runnable project
```

## Key APIs you build against (already implemented, do not change signatures)

- `IWorkflowStore` in `src/DurableLinq/IWorkflowStore.cs`. Read the XML docs; the two invariants are
  atomic append with duplicate rejection, and compare-and-set leases. `WaitForSignalAsync` has a
  default polling implementation; override it when the backend supports push (Redis BLPOP, Postgres LISTEN).
- `StepEntry(int Seq, string Name, byte[]? Payload, DateTimeOffset RecordedAt, bool Failed = false)`.
  Persist `Failed`.
- `InstanceHeader` record in `src/DurableLinq/Model.cs`. `UpdateStatusAsync(key, Completed, bytes)` sets
  `Result`; `(key, Failed, bytes)` sets `Error`; `(key, Running, null)` clears `Error`.
- `InstanceQuery` filter; `ListInstancesAsync` returns newest first (by `CreatedAt`), applies
  `Offset`/`Limit`, and `InstanceIdContains` is case-insensitive.
- `ListRunnableAsync(expiredBefore)` returns keys where `Status == Running` and
  (`LeaseExpires is null` or `LeaseExpires < expiredBefore`).
- `DeleteAsync(key)` removes header, steps, and signals; the key must be reusable afterwards.
- `DurableBuilder` in `src/DurableLinq.Hosting/DurableBuilder.cs`. Store packages add
  `public static DurableBuilder UseXxx(this DurableBuilder b, string connectionString, Action<XxxOptions>? configure = null)`
  in namespace `DurableLinq.Hosting` that calls `b.UseStore(sp => ...)`.
- `IWorkflowRunner`, `IWorkflowDefinitionRegistry` (Hosting) for the dashboard.

## Contract tests

Every store test project derives from `DurableLinq.Tests.Contract.WorkflowStoreContractTests`
(reference the `DurableLinq.Tests` project) and implements `CreateStoreAsync`. All contract tests must pass.
Use Testcontainers (`Testcontainers.Redis`, `Testcontainers.PostgreSql`, `Testcontainers.MsSql`, versions in
`Directory.Packages.props`) with a shared xUnit collection fixture so one container serves the whole class.
Docker Desktop is available on this machine. If Docker is not reachable at test time, make the fixture
skip the tests with a clear message instead of failing.

## Conventions

- .NET 10, C# latest, nullable on, `ConfigureAwait(false)` in library code.
- No new dependencies beyond: `StackExchange.Redis`, `Dapper`, `Npgsql`, `Microsoft.Data.SqlClient`,
  and the Microsoft.Extensions.* abstractions already listed. The core package stays dependency free.
- Add every new project to `DurableLinq.sln` with `dotnet sln add`.
- Build with `dotnet build` at the repo root and make sure the whole solution compiles warning-free
  for your projects before you finish. Run `dotnet test` for your test project.
- Do not run `git commit` (commit signing prompts for a passphrase and hangs). Leave files uncommitted.
- Keep it simple. The goal of the library is "drop it in and it works". Prefer one clear class over
  a plugin architecture.

## Defining workflows

The house style for apps and examples is a class per workflow: a sealed class implementing
`IWorkflow<TIn, TOut>` (`src/DurableLinq/IWorkflow.cs`) with dependencies as primary constructor
parameters, `static string Name`, `static int Version`, and the body in `Build`. `Build` is either a query
expression returning `Durable<TOut>` or an `async Durable<TOut>` method whose every `await` on a durable is a
step (`src/DurableLinq/DurableMethodBuilder.cs`). Both forms are first class; each example ships both, with
`WORKFLOW_STYLE=async` selecting the `async` class. New docs and examples show both unless the point is
specific to one. Register it
with `durable.AddWorkflow<TWorkflow, TIn, TOut>()` (`src/DurableLinq.Hosting/DurableBuilderWorkflowExtensions.cs`),
which activates one instance per run from that run's DI scope. `WorkflowDefinition.Create(name, version,
build)` (a lambda, no class) is for tests and other low-level use where standing up a DI container is
unnecessary.

## Formatting

Run `dotnet format` before you finish. Do not column-align the `in` keywords in query expressions; the formatter removes the extra spaces. Put each `from`, `let`, and `select` on its own line and let the vertical layout carry the structure.

## Failures

Business outcomes are values, not exceptions: `RunAsync`/`ResumeAsync` return `RunResult<TOut>` (`Completed`/`Rejected`/`Failed`/`Busy`) and never throw; use `ValueOrThrow()` for the one-line happy path. Validation of input goes in `Validate(TIn)` on the workflow class, returning a `ValidationResult` (a failing run is `Rejected`). Checks on step results use `.Ensure(...)` on the step (or a `where` clause), never a `from _ in` guard step; these fail the run with a terminal `validation` error. Throw `TerminalException(code, message)` (or implement `IWorkflowFailure`) for failures a retry cannot fix; any other exception is captured as a retryable error keyed by its type name. Designed failures use `Durable.Try(name, () => Outcome.Fail<T>(code, message, terminal))`. Handle failures with `.Recover(handler)` or `.Recover(code, handler)` on the durable, before the `from` or the `await`. In an `async Durable<T>` body a failed step throws `StepFailedException` at the `await`; leave it uncaught (the builder turns it into the failed outcome) and never `catch` it, because a `catch` records nothing and a retry may then take a different path. See the "Failures and validation" section of README.md.
