# Example 03: Redis Workers

Several worker hosts sharing one Redis store.

`Workflows/` holds `ShipmentWorkflow` (query expression) and `ShipmentAsyncWorkflow` (`async Durable<T>` method). Set `WORKFLOW_STYLE=async` in the worker environment to register the second. Both write the same log.

```
docker compose up --build          starts redis, two workers, and the dashboard on http://localhost:8080
docker compose kill worker-1       kill a worker mid-run; worker-2's recovery sweeper resumes its instances
docker compose up -d worker-1      bring it back; it picks up new work and never re-runs finished steps
```

Each worker produces "shipment" orders with ids based on its own name, then runs them. Because the
runner is idempotent per instance key, two workers that receive the same id take turns via the lease.
Locally without Docker: set `REDIS=localhost:6379` and run `dotnet run`.
