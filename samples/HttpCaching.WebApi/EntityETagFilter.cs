using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PgNotify.Caching;

namespace HttpCaching.WebApi;

/// <summary>
/// Makes a GET endpoint conditional: it derives an <c>ETag</c> from
/// <see cref="IEntityChangeTracker.LastModified"/> plus the request's query string, and
/// answers <c>304 Not Modified</c> when the client already has that version — so an unchanged
/// table costs one round trip with no body, no query, and no serialization.
/// </summary>
/// <remarks>
/// The MVC equivalent is an <c>ActionFilterAttribute</c> resolving
/// <c>IEntityChangeTracker&lt;TEntity&gt;</c> from <c>HttpContext.RequestServices</c> in
/// <c>OnActionExecutionAsync</c>; the logic below is identical.
/// </remarks>
internal sealed class EntityETagFilter<TEntity>(IEntityChangeTracker<TEntity> tracker) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var request = context.HttpContext.Request;
        var response = context.HttpContext.Response;

        // LastModified is the trigger's timestamp, not this process's clock, so every instance
        // behind a load balancer derives the *same* ETag for the same write — with a local
        // timestamp, a client routed to another instance would get a fresh 200 every time.
        var lastModified = tracker.LastModified;
        var etag = $"\"{lastModified.UtcTicks:x}-{QueryHash(request)}\"";

        // Headers are set before the 304 check so both responses carry them (a 304 must repeat the
        // ETag it validated).
        response.Headers.ETag = etag;
        response.Headers.LastModified = lastModified.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);
        response.Headers.CacheControl = "public, no-cache";

        return request.Headers.IfNoneMatch.Contains(etag) ? Results.StatusCode(StatusCodes.Status304NotModified) : await next(context);
    }

    private static string QueryHash(HttpRequest request)
    {
        if (request.Query.Count == 0)
        {
            return "noquery";
        }

        // Not string.GetHashCode(): .NET randomises string hashing per process, so it would change
        // the ETag of an unchanged resource on every restart and differ between instances — the
        // 304 path would then almost never hit. SHA-256 over a canonical (order-independent) form
        // is stable everywhere.
        var canonical = string.Join(
            '&',
            request.Query.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => $"{pair.Key}={pair.Value}"));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..8].ToLowerInvariant();
    }
}
