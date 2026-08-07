using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.EFCore.Tests.TestModels;

namespace PgNotify.EFCore.Tests;

public class NotificationValidationConventionTests
{
    [Fact]
    public void Entity_without_primary_key_throws_at_model_build_time()
    {
        // OrderLine has no Id/{Type}Id property, so EF Core's key-discovery convention leaves it
        // keyless unless a key is configured explicitly - which this test deliberately omits.
        var act = () => ConventionDbContext.BuildModel(mb =>
            mb.Entity<OrderLine>(b => b.HasDatabaseNotifications()));

        act.Should().Throw<InvalidOperationException>().WithMessage("*primary key*");
    }

    [Fact]
    public void Watching_a_navigation_property_throws_at_model_build_time()
    {
        var act = () => ConventionDbContext.BuildModel(mb =>
            mb.Entity<User>(b =>
            {
                b.HasMany(x => x.Orders).WithOne(x => x.User).HasForeignKey(x => x.UserId);
                b.HasDatabaseNotifications(o => o.OnUpdate(x => x.Orders));
            }));

        act.Should().Throw<InvalidOperationException>().WithMessage("*navigation*");
    }

    [Fact]
    public void Valid_configuration_does_not_throw()
    {
        var act = () => ConventionDbContext.BuildModel(mb =>
            mb.Entity<User>().HasDatabaseNotifications(o => o.OnUpdate(x => x.Name)));

        act.Should().NotThrow();
    }
}
