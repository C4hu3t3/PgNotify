using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CacheInvalidation.WebApi;

public static class ProductEndpoints
{
    public static string CacheKey(int id) => $"product:{id}";

    public static void MapProductEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/products");

        group.MapPost("/", async (ProductRequest request, SampleDbContext db) =>
        {
            var product = new Product { Name = request.Name, Price = request.Price };
            db.Products.Add(product);
            await db.SaveChangesAsync();
            return Results.Created($"/products/{product.Id}", product);
        });

        group.MapGet("/{id:int}", async (int id, SampleDbContext db, IMemoryCache cache, ILogger<Program> logger) =>
        {
            if (cache.TryGetValue(CacheKey(id), out Product? cached))
            {
                logger.LogInformation("Cache hit for product {ProductId}", id);
                return cached is null ? Results.NotFound() : Results.Ok(cached);
            }

            logger.LogInformation("Cache miss for product {ProductId}; querying the database", id);
            var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);

            // Cache both hits and misses (as null) with a safety-net expiry: the notification
            // handler is what normally keeps this fresh, the TTL just bounds staleness if a
            // notification is ever missed (e.g. the listener was down).
            cache.Set(CacheKey(id), product, TimeSpan.FromMinutes(5));

            return product is null ? Results.NotFound() : Results.Ok(product);
        });

        group.MapPut("/{id:int}", async (int id, ProductRequest request, SampleDbContext db) =>
        {
            var product = await db.Products.FindAsync(id);
            if (product is null)
            {
                return Results.NotFound();
            }

            product.Name = request.Name;
            product.Price = request.Price;
            await db.SaveChangesAsync();

            // No manual cache.Remove() here: ProductCacheInvalidationHandler does it, triggered
            // by the database's own notification, not by this request handler's own bookkeeping.
            return Results.Ok(product);
        });

        group.MapDelete("/{id:int}", async (int id, SampleDbContext db) =>
        {
            var product = await db.Products.FindAsync(id);
            if (product is null)
            {
                return Results.NotFound();
            }

            db.Products.Remove(product);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}

public sealed record ProductRequest(string Name, decimal Price);
