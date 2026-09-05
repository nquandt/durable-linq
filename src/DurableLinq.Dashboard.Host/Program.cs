using DurableLinq.Dashboard;
using DurableLinq.Hosting;
using DurableLinq.Postgres;
using DurableLinq.Redis;
using DurableLinq.SqlServer;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(int.Parse(port)));

var storeKind = (Environment.GetEnvironmentVariable("DURABLE_STORE") ?? "file").Trim().ToLowerInvariant();
var connection = Environment.GetEnvironmentVariable("DURABLE_CONNECTION");
var keyPrefix = Environment.GetEnvironmentVariable("DURABLE_KEY_PREFIX");

builder.Services.AddDurable(d =>
{
    if (!string.IsNullOrEmpty(keyPrefix)) d.Namespace = keyPrefix;
    d.EnableRecovery = false;

    switch (storeKind)
    {
        case "file":
            d.UseFileStore(connection ?? "/data");
            break;

        case "postgres":
            if (string.IsNullOrEmpty(connection))
                throw new InvalidOperationException("DURABLE_CONNECTION is required when DURABLE_STORE=postgres.");
            d.UsePostgres(connection);
            break;

        case "redis":
            if (string.IsNullOrEmpty(connection))
                throw new InvalidOperationException("DURABLE_CONNECTION is required when DURABLE_STORE=redis.");
            d.UseRedis(connection);
            break;

        case "sqlserver":
            if (string.IsNullOrEmpty(connection))
                throw new InvalidOperationException("DURABLE_CONNECTION is required when DURABLE_STORE=sqlserver.");
            d.UseSqlServer(connection);
            break;

        default:
            throw new InvalidOperationException($"Unknown DURABLE_STORE '{storeKind}'. Expected file, redis, postgres, or sqlserver.");
    }
});

var app = builder.Build();

app.MapDurableDashboard("/");

app.Run();
