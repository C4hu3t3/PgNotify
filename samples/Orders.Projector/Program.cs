using Microsoft.EntityFrameworkCore;
using Orders.Projector;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("OrdersDatabase")
    ?? "Host=localhost;Port=5434;Database=orders_sample;Username=postgres;Password=postgres";

// UseNpgsqlNotificationsListening(), not the full UseNpgsqlNotifications(): this context never
// runs dotnet ef, so it has no use for the migrations SQL-generator replacement the full method
// also wires up - only the marker AddNotificationMappingFromDbContexts() below looks for.
builder.Services.AddDbContext<ProjectorDbContext>(options => options
    .UseNpgsql(connectionString)
    .UseNpgsqlNotificationsListening());

builder.Services.AddSingleton<SummaryStore>();

builder.Services.AddPostgresNotifications(options =>
{
    options.AddHandlersFromAssembly(typeof(Program).Assembly);
    options.UseLogging();

    // No channel name and no connection string set above: both come from ProjectorDbContext's own
    // model — the same model-driven derivation Orders.WebApi uses on the write side, just running
    // a second, independent time against a second, independent DbContext mapping the same table.
    options.AddNotificationMappingFromDbContexts();
});

var app = builder.Build();

app.MapGet("/summary", (SummaryStore store) => store.Summarize());

app.Run();
