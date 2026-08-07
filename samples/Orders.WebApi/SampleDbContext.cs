using Microsoft.EntityFrameworkCore;
using Orders.Model;

namespace Orders.WebApi;

public class SampleDbContext(DbContextOptions<SampleDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}
