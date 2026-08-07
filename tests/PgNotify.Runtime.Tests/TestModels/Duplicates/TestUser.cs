namespace PgNotify.Runtime.Tests.TestModels.Duplicates;

/// <summary>
/// A second <c>TestUser</c>, in another namespace: exactly the shape that makes two entities
/// indistinguishable in a payload, whose <c>"entity"</c> field carries the short name only.
/// </summary>
public sealed class TestUser
{
    public int Id { get; set; }
}
