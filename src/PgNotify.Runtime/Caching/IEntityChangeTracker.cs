using Microsoft.Extensions.Primitives;

namespace PgNotify.Caching;

/// <summary>
/// Tracks when an entity's table last changed, as an <see cref="IChangeToken"/> suitable for
/// <c>IMemoryCache</c> expiration (<c>entry.AddExpirationToken(...)</c>), <c>ChangeToken.OnChange</c>,
/// or HTTP <c>ETag</c>/<c>Last-Modified</c> generation. Enabled by
/// <c>options.AddChangeTracking()</c>; every notification received for the entity — from any
/// operation — invalidates the current token.
/// </summary>
public interface IEntityChangeTracker
{
    /// <summary>
    /// When the entity's table last changed, in UTC. Taken from the trigger's own
    /// <c>timestamp</c> payload field when present (the default
    /// <c>ExtendedNotificationPayloadBuilder</c> emits one), so every process listening to the
    /// same database observes the same value — which is what makes an <c>ETag</c> derived from it
    /// stable across instances behind a load balancer. Falls back to local receive time for
    /// payloads without a timestamp. Never moves backwards.
    /// </summary>
    DateTimeOffset LastModified { get; }

    /// <summary>
    /// Returns a token that fires the next time a notification for this entity is received. The
    /// returned token signals at most once; call this again after it fires to observe subsequent
    /// changes.
    /// </summary>
    IChangeToken GetChangeToken();
}

/// <summary>
/// The <see cref="IEntityChangeTracker"/> for <typeparamref name="TEntity"/>, injectable directly
/// (<c>IEntityChangeTracker&lt;User&gt;</c>) once <c>options.AddChangeTracking()</c> is called.
/// </summary>
/// <remarks>
/// Trackers are keyed by the entity display name carried in the notification payload, which
/// <c>PgNotify.EFCore</c> derives from <c>IEntityType.ShortName()</c> — the same value as
/// <see cref="System.Reflection.MemberInfo.Name"/> for an ordinary top-level entity class. For an
/// entity whose display name differs from its CLR type name, resolve the tracker by name through
/// <see cref="IEntityChangeTrackerSource.Get(string)"/> instead.
/// </remarks>
public interface IEntityChangeTracker<TEntity> : IEntityChangeTracker;
