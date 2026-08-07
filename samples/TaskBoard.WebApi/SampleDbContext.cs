using Microsoft.EntityFrameworkCore;
using TaskBoard.Model;

namespace TaskBoard.WebApi;

public class SampleDbContext(DbContextOptions<SampleDbContext> options) : DbContext(options)
{
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
}
