# DurableLinq.Redis

`RedisWorkflowStore` — an `IWorkflowStore` backed by Redis, for running DurableLinq across more than one host.

## Register it

```csharp
services.AddDurable(durable =>
{
    durable.UseRedis("localhost:6379");
    // or: durable.UseRedis("localhost:6379", o => o.KeyPrefix = "myapp");
    // or, sharing a connection you already own:
    durable.UseRedis(existingConnectionMultiplexer);

    durable.Define<OrderRequest, Receipt>("checkout", 1, Checkout);
});
```

`RedisStoreOptions`:

- `KeyPrefix` — prefix on every Redis key this store touches. Defaults to `"durable"`.
- `Database` — logical Redis database index to select (`SELECT`). Defaults to the server default.

## Key layout

All keys are prefixed with `{KeyPrefix}` (default `durable`):

| Key | Type | Holds |
|---|---|---|
| `{prefix}:h:{instanceKey}` | hash | The full `InstanceHeader`: namespace, workflow name, version, instance id, status, input/result/error (base64), lease owner/token/expiry, created/updated timestamps. |
| `{prefix}:steps:{instanceKey}` | list | Step entries in sequence order, one JSON object per entry (`seq`, `name`, base64 `payload`, `recordedAt`, `failed`). |
| `{prefix}:sig:{instanceKey}:{signalName}` | list | Pending signal payloads (base64) for one signal name, FIFO via `RPUSH`/`LPOP`. |
| `{prefix}:signames:{instanceKey}` | set | Every signal name ever used for an instance, so `DeleteAsync` can remove all signal lists without a `SCAN`. |
| `{prefix}:instances` | sorted set | Every instance key, scored by `CreatedAt` ticks — backs `ListInstancesAsync` (headers are filtered/paged in memory after the key list comes back). |
| `{prefix}:running` | sorted set | Instance keys with `Status == Running`, scored by lease-expiry ticks (`0` when there is no lease) — backs `ListRunnableAsync` via `ZRANGEBYSCORE`. |

## Atomicity

- **Step append** (`AppendStepAsync`) runs a Lua script that compares the submitted sequence number against `LLEN` of the
  steps list: equal appends, lower throws `DuplicateStepException`, higher throws `DurableException` (a gap).
- **Leases** (`TryAcquireLeaseAsync` / `RenewLeaseAsync` / `ReleaseLeaseAsync`) are Lua compare-and-set scripts against the
  `leaseOwner`/`leaseToken`/`leaseExpires` fields of the header hash, keeping the `{prefix}:running` sorted set's score in
  sync so the recovery sweeper sees the change immediately.
- **`WaitForSignalAsync`** polls `TryTakeSignalAsync` with a short backoff (100ms up to 500ms) rather than blocking the
  connection, since a `BLPOP`-style wait doesn't compose with a shared multiplexer serving many instances at once.

## Requirements

Redis 5+ (for `HSET` with multiple field/value pairs and Lua `redis.call`). No Redis modules required.
