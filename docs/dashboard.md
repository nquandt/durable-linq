# Dashboard

`DurableLinq.Dashboard` maps a small, self-contained web UI and JSON API for inspecting workflow
instances: their status, step history, and errors.

## Mount it in an existing app

```csharp
app.MapDurableDashboard("/durable");
```

`MapDurableDashboard` resolves `IWorkflowStore` (required), `IWorkflowRunner` (optional), and
`IWorkflowDefinitionRegistry` (optional) from DI. It needs no configuration beyond a mounted store.

## Run it standalone

`DurableLinq.Dashboard.Host` is a standalone container that connects to any store through environment
variables, for teams that want the dashboard next to the store instead of inside every app that uses it.

```
docker compose -f docker-compose.dashboard.yml up
```

See the [`03-Redis-Workers` example](examples/index.md) for the dashboard running alongside several
worker hosts, and the [`04-Postgres-Approvals` example](examples/index.md) for the dashboard mounted
in-process.
