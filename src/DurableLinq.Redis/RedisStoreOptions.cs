namespace DurableLinq.Redis;

/// <summary>Configuration for <see cref="RedisWorkflowStore"/>.</summary>
public sealed class RedisStoreOptions
{
    /// <summary>Prefix applied to every Redis key this store touches. Defaults to <c>"durable"</c>.</summary>
    public string KeyPrefix { get; set; } = "durable";

    /// <summary>Redis logical database index to select. Defaults to the server default (0).</summary>
    public int Database { get; set; } = -1;
}
