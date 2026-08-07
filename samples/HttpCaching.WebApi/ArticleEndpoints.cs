using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PgNotify.Caching;

namespace HttpCaching.WebApi;

public static class ArticleEndpoints
{
    public static void MapArticleEndpoints(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapGroup("/articles");

        // The two layers this sample is about, on one endpoint: the filter answers 304 when the
        // client's copy is still current, and the handler's cache entry lives until PostgreSQL
        // says the table changed.
        group.MapGet("/", GetArticles).AddEndpointFilter<EntityETagFilter<Article>>();

        // Shows the tracker's own state; handy to watch while running the curl script in README.md.
        group.MapGet("/state", (IEntityChangeTracker<Article> tracker) =>
            Results.Ok(new { lastModified = tracker.LastModified }));

        group.MapPost("/", async (ArticleRequest request, SampleDbContext db) =>
        {
            var article = new Article { Title = request.Title, Tag = request.Tag };
            db.Articles.Add(article);
            await db.SaveChangesAsync();
            return Results.Created($"/articles/{article.Id}", article);
        });

        group.MapPut("/{id:int}", async (int id, ArticleRequest request, SampleDbContext db) =>
        {
            var article = await db.Articles.FindAsync(id);
            if (article is null)
            {
                return Results.NotFound();
            }

            article.Title = request.Title;
            article.Tag = request.Tag;
            await db.SaveChangesAsync();

            // Nothing invalidates the cache here: the database's own trigger does, via the
            // notification listener — which is also why a change made by psql, another instance,
            // or a batch job invalidates it just the same.
            return Results.Ok(article);
        });

        group.MapDelete("/{id:int}", async (int id, SampleDbContext db) =>
        {
            var article = await db.Articles.FindAsync(id);
            if (article is null)
            {
                return Results.NotFound();
            }

            db.Articles.Remove(article);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    private static async Task<IResult> GetArticles(
        string? tag,
        SampleDbContext db,
        IMemoryCache cache,
        IEntityChangeTracker<Article> tracker,
        ILogger<Article> logger)
    {
        var cacheKey = $"articles:{tag ?? "*"}";

        var articles = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            // The whole point: the entry is valid until the table changes, however long that is,
            // instead of guessing a TTL. One token covers every cache key derived from the
            // entity, so every variant is dropped together on the next notification.
            entry.AddExpirationToken(tracker.GetChangeToken());

            // Safety net only, in case a notification is ever missed (listener down mid-write).
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);

            logger.LogInformation("Cache miss for {CacheKey}; querying the database", cacheKey);

            return await db.Articles
                .AsNoTracking()
                .Where(article => tag == null || article.Tag == tag)
                .OrderBy(article => article.Id)
                .ToListAsync();
        });

        return Results.Ok(articles ?? []);
    }
}

public sealed record ArticleRequest(string Title, string Tag = "general");
