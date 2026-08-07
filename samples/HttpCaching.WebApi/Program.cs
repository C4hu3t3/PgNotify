using HttpCaching.WebApi;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("SampleDatabase")
    ?? "Host=localhost;Database=http_caching_sample;Username=postgres;Password=postgres";

builder.Services.AddDbContext<SampleDbContext>(options => options
    .UseNpgsql(connectionString)
    .UseNpgsqlNotifications());

builder.Services.AddMemoryCache();

builder.Services.AddPostgresNotifications(options =>
{
    // Everything the cache needs. The window collapses the burst of row-level notifications a bulk
    // UPDATE produces into one leading and one trailing invalidation instead of one per row.
    options.AddChangeTracking(TimeSpan.FromMilliseconds(200));

    options.UseLogging();

    // This sample has no handler and no stream at all - change tracking works off the raw
    // envelope, as middleware - so the only thing the listener needs is which channels to LISTEN
    // on, and that comes from SampleDbContext's model. The connection string comes with it.
    options.AddNotificationMappingFromDbContexts();
});

var app = builder.Build();

// No `dotnet ef migrations` and no Docker in this sample: EnsureCreated creates the database and
// the Article table if they don't exist, then EnsureNotificationTriggers applies the trigger and
// trigger function — the database-first path, idempotent and a no-op once they're up to date.
using (var scope = app.Services.CreateScope())
{
    var database = scope.ServiceProvider.GetRequiredService<SampleDbContext>().Database;
    await database.EnsureCreatedAsync();
    await database.EnsureNotificationTriggersAsync();
}

app.MapArticleEndpoints();
app.MapHealthChecks("/health");

app.Run();
