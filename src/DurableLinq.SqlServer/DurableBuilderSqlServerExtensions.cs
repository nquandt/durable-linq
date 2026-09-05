using DurableLinq.SqlServer;
using Microsoft.Extensions.DependencyInjection;

namespace DurableLinq.Hosting;

/// <summary>Registers <see cref="SqlServerWorkflowStore"/> with <see cref="DurableBuilder"/>.</summary>
public static class DurableBuilderSqlServerExtensions
{
    public static DurableBuilder UseSqlServer(this DurableBuilder builder, string connectionString, Action<SqlServerStoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionString);
        var options = new SqlServerStoreOptions();
        configure?.Invoke(options);
        return builder.UseStore(sp => new SqlServerWorkflowStore(connectionString, options, sp.GetService<TimeProvider>()));
    }
}
