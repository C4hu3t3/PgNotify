using Microsoft.Extensions.Caching.Memory;
using PgNotify;
using PgNotify.Serialization;

namespace CacheInvalidation.WebApi;

/// <summary>
/// Evicts the cached product whenever the database reports it changed — including changes made
/// by *other* processes/instances, which an in-process-only cache invalidation strategy can never
/// catch. Keyed on <see cref="Product"/> itself, so one method covers insert, update and delete;
/// which channel carries them is stated once, by <c>MapChannel&lt;Product&gt;</c> in Program.cs.
/// </summary>
public sealed class ProductCacheInvalidationHandler(IMemoryCache cache, ILogger<ProductCacheInvalidationHandler> logger)
    : IDatabaseNotificationHandler<Product>
{
    public Task HandleAsync(NotificationEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        // The minimal payload puts the key at the top level, and the deserializer normalizes it
        // into Keys["id"] whatever the payload shape - so this reads the same for all three
        // operations, including a delete, whose row is already gone.
        var id = envelope.Keys["id"].GetInt32();

        cache.Remove(ProductEndpoints.CacheKey(id));
        logger.LogInformation(
            "Evicted cache entry for product {ProductId} after a {Operation} notification", id, envelope.Operation);

        return Task.CompletedTask;
    }
}
