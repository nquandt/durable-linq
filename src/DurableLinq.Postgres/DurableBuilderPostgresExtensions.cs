using DurableLinq.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace DurableLinq.Hosting;

/// <summary>Registers <see cref="PostgresWorkflowStore"/> with <see cref="DurableBuilder"/>.</summary>
public static class DurableBuilderPostgresExtensions
{
    public static DurableBuilder UsePostgres(this DurableBuilder builder, string connectionString, Action<PostgresStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionString);
        var options = new PostgresStoreOptions();
        configure?.Invoke(options);
        return builder.UseStore(sp => new PostgresWorkflowStore(connectionString, options, sp.GetService<TimeProvider>()));
    }
}
