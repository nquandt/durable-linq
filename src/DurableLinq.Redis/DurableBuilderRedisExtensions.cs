using DurableLinq.Redis;
using StackExchange.Redis;

namespace DurableLinq.Hosting;

/// <summary>Registers <see cref="RedisWorkflowStore"/> with a <see cref="DurableBuilder"/>.</summary>
public static class DurableBuilderRedisExtensions
{
    /// <summary>Uses Redis as the workflow store, connecting with <paramref name="connectionString"/>.</summary>
    public static DurableBuilder UseRedis(this DurableBuilder builder, string connectionString, Action<RedisStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionString);
        var options = new RedisStoreOptions();
        configure?.Invoke(options);
        return builder.UseStore(_ => new RedisWorkflowStore(connectionString, options));
    }

    /// <summary>Uses Redis as the workflow store over an existing, shared <see cref="IConnectionMultiplexer"/>.</summary>
    public static DurableBuilder UseRedis(this DurableBuilder builder, IConnectionMultiplexer multiplexer, Action<RedisStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(multiplexer);
        var options = new RedisStoreOptions();
        configure?.Invoke(options);
        return builder.UseStore(_ => new RedisWorkflowStore(multiplexer, options));
    }
}
