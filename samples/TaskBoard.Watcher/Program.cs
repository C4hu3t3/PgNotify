using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TaskBoard.Model;
using TaskBoard.Watcher;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("TaskBoardDatabase")
    ?? "Host=localhost;Port=5433;Database=taskboard_sample;Username=postgres;Password=postgres";

builder.Services.AddPostgresNotifications(options =>
{
    options.ConnectionString = connectionString;
    options.UseLogging();

    // This process has no DbContext to derive the mapping from, so it states it: TaskItem's
    // notifications arrive on the "TaskItem" channel, which is the table TaskBoard.Model pins.
    // One line, one channel - and the entity type it names is the same POCO the streams below and
    // any handler would key on.
    options.MapChannel<TaskItem>("TaskItem");
});

builder.Services.AddHostedService<NotificationPrinter>();

var host = builder.Build();
await host.RunAsync();
