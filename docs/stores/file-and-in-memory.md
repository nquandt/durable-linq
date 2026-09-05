# File and in-memory stores

Both stores ship in the core `DurableLinq` package. No configuration and no external service.

## In-memory

Dictionaries behind a lock. State is lost when the process exits. Use it for unit tests and for the
first ten minutes with the library.

```csharp
var runner = new WorkflowRunner(new InMemoryStore());
```

Or with hosting:

```csharp
durable.UseInMemoryStore();
```

## File

One directory per instance key, containing `header.json`, `steps.jsonl` (append with write-through and
fsync), and `signals/`. Leases are the header's lease fields rewritten with a temp-file-and-rename.

```csharp
durable.UseFileStore("./workflow-state");
```

Single-process only, since file locks aren't reliable across hosts. Good for local development and for
small single-node services. See the [Quick start](../getting-started.md) for a full example, including
killing the process mid-run to see a replay.

Move to [Redis](redis.md), [PostgreSQL](postgres.md), or [SQL Server](sqlserver.md) when a second host
needs to share the store.
