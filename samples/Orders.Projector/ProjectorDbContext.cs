using Microsoft.EntityFrameworkCore;
using Orders.Model;

namespace Orders.Projector;

/// <summary>
/// Maps the same <c>Order</c> table as <c>Orders.WebApi</c>'s <c>SampleDbContext</c>, from an
/// entirely separate process and project. Used two ways: to derive this process' notification
/// channel/connection string (<c>AddNotificationMappingFromDbContexts()</c> in Program.cs), and by
/// <see cref="OrderProjectionHandler"/> to re-read a row after a notification names it.
/// </summary>
/// <remarks>
/// Never migrated from here — <c>Orders.WebApi</c> owns the schema and the trigger. This context
/// only ever reads.
/// </remarks>
public class ProjectorDbContext(DbContextOptions<ProjectorDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}
