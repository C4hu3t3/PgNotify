using Microsoft.EntityFrameworkCore;

namespace CacheInvalidation.WebApi;

public class SampleDbContext(DbContextOptions<SampleDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
}
