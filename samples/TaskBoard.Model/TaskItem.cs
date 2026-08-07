using System.ComponentModel.DataAnnotations.Schema;
using PgNotify;

namespace TaskBoard.Model;

/// <summary>
/// The one thing <c>TaskBoard.WebApi</c> (which maps this with EF Core and owns the migration) and
/// <c>TaskBoard.Watcher</c> (which never touches EF Core at all) share — a plain entity class.
/// [NotifyChanges] alone enables insert/update/delete notifications with the minimal payload
/// (<c>{"entity":"TaskItem","operation":"...","id":...}</c>), and the watcher keys its streams on
/// this very type: <c>Events&lt;TaskItem&gt;()</c>.
/// </summary>
/// <remarks>
/// [Table("TaskItem")] is here for one reason, and it is a cross-process one: the watcher has no
/// model to read, so it has to name the channel itself, and the channel is the table name. Pinning
/// the table in the shared project keeps that name from being decided by a <c>DbSet</c> property in
/// a project the watcher does not reference (<c>Tasks</c> would make the channel "Tasks"). A process
/// that <i>does</i> have the <c>DbContext</c> needs none of this — see
/// <c>CacheInvalidation.WebApi</c>, where <c>options.AddNotificationMappingFromDbContexts()</c> reads the
/// channel out of the model and the entity carries no [Table] at all.
/// </remarks>
[NotifyChanges]
[Table("TaskItem")]
public class TaskItem
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public bool IsDone { get; set; }
}
