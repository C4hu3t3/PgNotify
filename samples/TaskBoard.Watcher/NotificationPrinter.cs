using Microsoft.Extensions.Hosting;
using PgNotify;
using TaskBoard.Model;

namespace TaskBoard.Watcher;

/// <summary>Prints every TaskItem notification received to the console, one line per event.</summary>
public sealed class NotificationPrinter(IPostgresNotificationService notifications) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Console.WriteLine("Listening for TaskItem notifications. Press Ctrl+C to exit.");

        // Three streams over one entity, split by operation. A single Events<TaskItem>() loop with
        // a switch on envelope.Operation would do as well - the operation is in the payload either
        // way, and both forms read the same channel.
        return Task.WhenAll(
            Print(NotificationOperation.Insert, "+ created"),
            Print(NotificationOperation.Update, "~ updated"),
            Print(NotificationOperation.Delete, "- deleted"));

        async Task Print(NotificationOperation operation, string label)
        {
            await foreach (var envelope in notifications.Events<TaskItem>(operation, stoppingToken))
            {
                Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {label} #{envelope.Keys["id"].GetInt32()}");
            }
        }
    }
}
