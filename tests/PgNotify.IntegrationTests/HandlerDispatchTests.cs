using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Serialization;
using PgNotify.IntegrationTests.TestModels;

namespace PgNotify.IntegrationTests;

/// <summary>
/// Exercises the class-based consumption path end-to-end (as distinct from the
/// <c>Events&lt;TEntity&gt;()</c> streams covered in
/// <see cref="NotificationEndToEndTests"/>): the three operation-specific handler interfaces, the
/// entity-wide one, and the non-generic catch-all, all keyed on the entity type rather than on a
/// generated event type. Uses its own dedicated host since it needs handler classes registered via
/// <c>AddHandlersFromAssembly</c>.
/// </summary>
[Collection(nameof(AssemblyScannedHandlerCollection))]
public sealed class HandlerDispatchTests : IAsyncLifetime
{
    private IntegrationHost _host = null!;

    // Every entry is "<role>:<id>", appended in invocation order. AddHandlersFromAssembly registers
    // these handlers into every other host that scans this assembly too, so they stay inert until
    // this test arms them (see AssemblyScannedHandlerCollection).
    private static readonly ConcurrentQueue<string> Log = new();
    private static volatile bool _armed;

    public async Task InitializeAsync()
    {
        Log.Clear();
        _armed = true;
        _host = await IntegrationHost.StartAsync(o => o.AddHandlersFromAssembly(typeof(InsertedHandler).Assembly));
    }

    public async Task DisposeAsync()
    {
        _armed = false;
        await _host.DisposeAsync();
    }

    private static void Record(string role, NotificationEnvelope envelope)
    {
        if (_armed)
        {
            Log.Enqueue($"{role}:{envelope.Keys["id"].GetInt32()}");
        }
    }

    private sealed class InsertedHandler : IDatabaseInsertedHandler<TestUser>
    {
        public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
        {
            Record("inserted", envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class UpdatedHandler : IDatabaseUpdatedHandler<TestUser>
    {
        public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
        {
            Record("updated", envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class DeletedHandler : IDatabaseDeletedHandler<TestUser>
    {
        public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
        {
            Record("deleted", envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class EntityWideHandler : IDatabaseNotificationHandler<TestUser>
    {
        public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
        {
            Record($"entity-{envelope.Operation}", envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class CatchAllHandler : IDatabaseNotificationHandler
    {
        public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
        {
            Record($"catch-all-{envelope.Operation}", envelope);
            return Task.CompletedTask;
        }
    }

    private static async Task<IReadOnlyList<string>> WaitForAsync(int userId, int expectedEntries)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var entries = Log.Where(e => e.EndsWith($":{userId}", StringComparison.Ordinal)).ToArray();
            if (entries.Length >= expectedEntries)
            {
                return entries;
            }

            await Task.Delay(50);
        }

        return [.. Log.Where(e => e.EndsWith($":{userId}", StringComparison.Ordinal))];
    }

    [Fact]
    public async Task Each_operation_reaches_its_own_handler_the_entity_wide_one_and_the_catch_all()
    {
        var options = new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql(_host.ConnectionString).Options;
        await using var context = new IntegrationDbContext(options);
        var user = new TestUser { Name = "Barbara", Email = "barbara@example.com" };

        context.Users.Add(user);
        await context.SaveChangesAsync();

        var afterInsert = await WaitForAsync(user.Id, 3);
        afterInsert.Should().Equal($"inserted:{user.Id}", $"entity-Insert:{user.Id}", $"catch-all-Insert:{user.Id}");

        user.Name = "Barbara Liskov";
        await context.SaveChangesAsync();

        var afterUpdate = await WaitForAsync(user.Id, 6);
        afterUpdate.Skip(3).Should().Equal($"updated:{user.Id}", $"entity-Update:{user.Id}", $"catch-all-Update:{user.Id}");

        context.Users.Remove(user);
        await context.SaveChangesAsync();

        var afterDelete = await WaitForAsync(user.Id, 9);
        afterDelete.Skip(6).Should().Equal($"deleted:{user.Id}", $"entity-Delete:{user.Id}", $"catch-all-Delete:{user.Id}");
    }
}
