using PgNotify;

namespace CacheInvalidation.WebApi;

/// <summary>
/// [NotifyChanges] alone is enough: it enables insert/update/delete notifications with the minimal
/// payload (entity/operation/id), and nothing else in this sample names a channel — the listener
/// derives it from this entity's mapped table, whatever EF Core decides that is.
/// </summary>
[NotifyChanges]
public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public bool IsAvailable { get; set; } = true;
}
