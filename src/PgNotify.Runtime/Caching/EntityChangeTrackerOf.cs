using Microsoft.Extensions.Primitives;

namespace PgNotify.Caching;

/// <summary>
/// Adapts the name-keyed <see cref="EntityChangeTrackerRegistry"/> to the injectable
/// <see cref="IEntityChangeTracker{TEntity}"/>, registered as an open generic singleton so
/// <c>IEntityChangeTracker&lt;User&gt;</c> resolves without any per-entity registration.
/// </summary>
internal sealed class EntityChangeTrackerOf<TEntity>(EntityChangeTrackerRegistry registry) : IEntityChangeTracker<TEntity>
{
    private readonly IEntityChangeTracker _tracker = registry.Get(typeof(TEntity).Name);

    public DateTimeOffset LastModified => _tracker.LastModified;

    public IChangeToken GetChangeToken() => _tracker.GetChangeToken();
}
