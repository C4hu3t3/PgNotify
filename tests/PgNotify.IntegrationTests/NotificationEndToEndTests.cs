using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.IntegrationTests.TestModels;

namespace PgNotify.IntegrationTests;

[Collection(nameof(NotificationHostCollection))]
public class NotificationEndToEndTests(NotificationHostFixture fixture)
{
    private IntegrationDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<IntegrationDbContext>().UseNpgsql(fixture.Host.ConnectionString).Options);

    [Fact]
    public async Task Inserting_a_row_notifies_the_entitys_stream()
    {
        await using var context = CreateContext();
        var user = new TestUser { Name = "Ada", Email = "ada@example.com" };

        var waitTask = NotificationWaiter.WaitAsync<TestUser>(fixture.Host.Notifications, NotificationOperation.Insert);

        context.Users.Add(user);
        await context.SaveChangesAsync();

        (await waitTask).Id().Should().Be(user.Id);
    }

    [Fact]
    public async Task Updating_a_watched_column_notifies()
    {
        await using var context = CreateContext();
        var user = new TestUser { Name = "Grace", Email = "grace@example.com" };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var waitTask = NotificationWaiter.WaitAsync<TestUser>(
            fixture.Host.Notifications, NotificationOperation.Update, e => e.Id() == user.Id);

        user.Name = "Grace Hopper";
        await context.SaveChangesAsync();

        (await waitTask).Id().Should().Be(user.Id);
    }

    [Fact]
    public async Task Updating_an_unwatched_column_does_not_notify()
    {
        await using var context = CreateContext();
        var user = new TestUser { Name = "Linus", Email = "linus@example.com" };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        var waitTask = NotificationWaiter.TryWaitAsync<TestUser>(
            fixture.Host.Notifications, NotificationOperation.Update, e => e.Id() == user.Id, NotificationWaiter.AbsenceTimeout);

        // Email is mapped but not in the OnUpdate(x => x.Name) watch list.
        user.Email = "linus@newdomain.example.com";
        await context.SaveChangesAsync();

        (await waitTask).Should().BeNull("only the watched Name column should trigger a notification");
    }

    [Fact]
    public async Task Deleting_a_row_notifies_with_the_key_read_from_OLD()
    {
        await using var context = CreateContext();
        var user = new TestUser { Name = "Katherine", Email = "katherine@example.com" };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var userId = user.Id;

        var waitTask = NotificationWaiter.WaitAsync<TestUser>(
            fixture.Host.Notifications, NotificationOperation.Delete, e => e.Id() == userId);

        context.Users.Remove(user);
        await context.SaveChangesAsync();

        (await waitTask).Id().Should().Be(userId);
    }
}
