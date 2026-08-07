using PgNotify;

namespace PgNotify.Core.Tests;

public class NotificationOperationExtensionsTests
{
    [Fact]
    public void Expand_returns_operations_in_insert_update_delete_order()
    {
        var operations = NotificationOperations.Delete | NotificationOperations.Insert | NotificationOperations.Update;

        operations.Expand().Should().Equal(
            NotificationOperation.Insert,
            NotificationOperation.Update,
            NotificationOperation.Delete);
    }

    [Fact]
    public void Expand_of_None_yields_nothing()
    {
        NotificationOperations.None.Expand().Should().BeEmpty();
    }

    [Theory]
    [InlineData(NotificationOperation.Insert, "INSERT")]
    [InlineData(NotificationOperation.Update, "UPDATE")]
    [InlineData(NotificationOperation.Delete, "DELETE")]
    public void ToSqlKeyword_and_ParseSqlKeyword_round_trip(NotificationOperation operation, string keyword)
    {
        operation.ToSqlKeyword().Should().Be(keyword);
        NotificationOperationExtensions.ParseSqlKeyword(keyword).Should().Be(operation);
    }

    [Theory]
    [InlineData(NotificationOperation.Insert, "created")]
    [InlineData(NotificationOperation.Update, "updated")]
    [InlineData(NotificationOperation.Delete, "deleted")]
    public void ToPastTenseWord_and_TryParsePastTenseWord_round_trip(NotificationOperation operation, string word)
    {
        operation.ToPastTenseWord().Should().Be(word);

        NotificationOperationExtensions.TryParsePastTenseWord(word, out var parsed).Should().BeTrue();
        parsed.Should().Be(operation);
    }

    [Fact]
    public void TryParsePastTenseWord_returns_false_for_unknown_value()
    {
        NotificationOperationExtensions.TryParsePastTenseWord("bogus", out _).Should().BeFalse();
    }

    [Fact]
    public void ToFlag_maps_each_single_operation()
    {
        NotificationOperation.Insert.ToFlag().Should().Be(NotificationOperations.Insert);
        NotificationOperation.Update.ToFlag().Should().Be(NotificationOperations.Update);
        NotificationOperation.Delete.ToFlag().Should().Be(NotificationOperations.Delete);
    }
}
