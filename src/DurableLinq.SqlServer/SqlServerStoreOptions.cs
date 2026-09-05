namespace DurableLinq.SqlServer;

/// <summary>Configuration for <see cref="SqlServerWorkflowStore"/>.</summary>
public sealed class SqlServerStoreOptions
{
    /// <summary>SQL Server schema that holds the DurableLinq tables. Created automatically when <see cref="AutoCreateSchema"/> is true.</summary>
    public string Schema { get; set; } = "durable";

    /// <summary>Optional prefix applied to every table name. Lets multiple installations share one schema.</summary>
    public string? TablePrefix { get; set; }

    /// <summary>Whether the schema and tables are created on first use. Defaults to <c>true</c>.</summary>
    public bool AutoCreateSchema { get; set; } = true;
}
