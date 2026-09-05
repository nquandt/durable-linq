# Example 04: Postgres Approvals

A human-in-the-loop approval workflow on Postgres, with the dashboard mounted in-process.

`Workflows/` holds `ExpenseApprovalWorkflow` (query expression) and `ExpenseApprovalAsyncWorkflow` (`async Durable<T>` method). Set `WORKFLOW_STYLE=async` to register the second. The async version branches with a plain `if`, so its log has no `if` entry; do not switch styles while an approval is in flight.

```
docker compose up --build         postgres + this app on http://localhost:5004 (dashboard at /durable)
curl -X POST localhost:5004/expenses/e-1 -H "content-type: application/json" -d '{"employee":"kim","amount":1200}'
curl -X POST localhost:5004/expenses/e-1/approve -H "content-type: application/json" -d '{"by":"lee","comment":"ok"}'
curl localhost:5004/expenses/e-1
```

Amounts under 500 are auto-approved (the If branch is recorded). Larger ones wait up to 2 minutes for
a manager decision, then escalate. Open http://localhost:5004/durable to watch the steps arrive.

Locally without Docker: set `POSTGRES` to a connection string and run `dotnet run`.
