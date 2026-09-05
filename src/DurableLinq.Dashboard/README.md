# DurableLinq.Dashboard

`MapDurableDashboard(...)` maps a small, self-contained web dashboard and JSON API for inspecting
DurableLinq workflow instances: status, step history, signals, and errors.

```csharp
app.MapDurableDashboard("/durable");
```

Full documentation is at <https://nquandt.github.io/durable-linq/dashboard/>.
