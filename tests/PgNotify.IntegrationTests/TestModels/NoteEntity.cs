namespace PgNotify.IntegrationTests.TestModels;

/// <summary>
/// Used only by the "watch-all-columns fingerprint reacts to the table's real column set, not
/// just configuration" test. <see cref="Body"/> exists on the physical table
/// (<c>db_first_notes</c>) from the start, but is deliberately unmapped by
/// <see cref="NotesDbContext"/> - <see cref="NotesDbContextWithBody"/> maps it, simulating a
/// database-first table whose real schema has outpaced one particular deployment's EF Core model.
/// </summary>
public class NoteEntity
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
}
