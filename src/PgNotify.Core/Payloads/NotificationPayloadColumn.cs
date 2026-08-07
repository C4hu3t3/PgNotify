namespace PgNotify.Payloads;

/// <summary>
/// One column selected by <c>WithPayload(x =&gt; new { ... })</c>: the CLR property that named it
/// and the database column its value is read from.
/// </summary>
/// <remarks>
/// The two are kept apart because they serve different consumers. The trigger reads
/// <paramref name="ColumnName"/> off <c>NEW</c>/<c>OLD</c>, while the JSON key that ends up in the
/// payload is <paramref name="PropertyName"/> — the payload is deserialized into a .NET event
/// type, so it has to speak that type's vocabulary rather than the storage layer's. Using the
/// column name for both silently unbinds every member the moment a column is renamed, whether by
/// <c>HasColumnName("full_name")</c> or wholesale by a package like
/// <c>EFCore.NamingConventions</c>.
/// </remarks>
/// <param name="PropertyName">The CLR property name, used as the payload's JSON key.</param>
/// <param name="ColumnName">The mapped column name, read by the generated trigger.</param>
public sealed record NotificationPayloadColumn(string PropertyName, string ColumnName);
