using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Orders.WebApi;

/// <summary>
/// Lets `dotnet ef migrations add` construct <see cref="SampleDbContext"/> without running the
/// full web host. The connection string here only needs to be valid enough for design-time model
/// building (migrations SQL generation doesn't actually connect to the database).
/// </summary>
public class SampleDbContextFactory : IDesignTimeDbContextFactory<SampleDbContext>
{
    public SampleDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SampleDbContext>()
            .UseNpgsql("Host=localhost;Port=5434;Database=orders_sample;Username=postgres;Password=postgres")
            .UseNpgsqlNotifications();

        return new SampleDbContext(optionsBuilder.Options);
    }
}
