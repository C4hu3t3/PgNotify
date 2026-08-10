namespace PgNotify.Caching;

/// <summary>
/// Resolves <see cref="IEntityChangeTracker"/> instances by entity name, for code that cannot name
/// the entity type statically (a generic cache helper, a controller filter, ...). Registered as a
/// singleton by <c>options.AddChangeTracking()</c>.
/// </summary>
public interface IEntityChangeTrackerSource
{
    /// <summary>
    /// The tracker for <paramref name="entityName"/> — the entity display name as it appears in
    /// the notification payload's <c>entity</c> field (case-sensitive). Creating a tracker for an
    /// entity that never notifies is harmless: it simply never invalidates. If, by the time this is
    /// called, the host has already resolved its notification mapping and no channel maps to
    /// <paramref name="entityName"/> at all — so it structurally never can notify, not just hasn't
    /// yet — a warning is logged once for that name, the same diagnostic a registered handler with
    /// no mapped channel already gets.
    /// </summary>
    IEntityChangeTracker Get(string entityName);

    /// <summary>The tracker for <typeparamref name="TEntity"/>, resolved by its CLR type name.</summary>
    IEntityChangeTracker Get<TEntity>() => Get(typeof(TEntity).Name);
}
