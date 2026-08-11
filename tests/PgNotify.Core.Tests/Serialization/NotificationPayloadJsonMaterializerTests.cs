using System.Text.Json;
using PgNotify;
using PgNotify.Model;
using PgNotify.Naming;
using PgNotify.Payloads;
using PgNotify.Serialization;

namespace PgNotify.Core.Tests.Serialization;

public class NotificationPayloadJsonMaterializerTests
{
    private static NotificationEntityConfiguration CreateConfiguration(
        INotificationPayloadBuilder? payloadBuilder = null,
        IReadOnlyList<string>? watchedUpdateColumns = null,
        IReadOnlyList<string>? keyColumns = null,
        bool unconditionalUpdate = false) => new()
    {
        EntityDisplayName = "Order",
        Schema = "public",
        TableName = "orders",
        Operations = NotificationOperations.All,
        KeyColumns = keyColumns ?? ["Id"],
        WatchedUpdateColumns = watchedUpdateColumns ?? [],
        UnconditionalUpdate = unconditionalUpdate,
        ChannelStrategy = PerEntityChannelNamingStrategy.Instance,
        PayloadBuilder = payloadBuilder ?? MinimalNotificationPayloadBuilder.Instance,
    };

    [Fact]
    public void Minimal_payload_on_insert_matches_what_the_deserializer_expects()
    {
        var config = CreateConfiguration();
        var json = NotificationPayloadJsonMaterializer.Materialize(
            config,
            NotificationOperation.Insert,
            currentValues: new Dictionary<string, object?> { ["Id"] = 42, ["Status"] = "New" },
            previousValues: null,
            timestamp: DateTimeOffset.UtcNow);

        var envelope = DefaultNotificationPayloadDeserializer.Instance.Deserialize("orders", json);

        envelope.Entity.Should().Be("Order");
        envelope.Operation.Should().Be(NotificationOperation.Insert);
        envelope.Keys.Should().ContainSingle().Which.Key.Should().Be("id");
        envelope.Keys["id"].GetInt32().Should().Be(42);
    }

    [Fact]
    public void Composite_key_uses_the_keys_object()
    {
        var config = CreateConfiguration(keyColumns: ["OrderId", "LineNumber"]);
        var json = NotificationPayloadJsonMaterializer.Materialize(
            config,
            NotificationOperation.Insert,
            currentValues: new Dictionary<string, object?> { ["OrderId"] = 1, ["LineNumber"] = 2 },
            previousValues: null,
            timestamp: DateTimeOffset.UtcNow);

        var envelope = DefaultNotificationPayloadDeserializer.Instance.Deserialize("orders", json);

        envelope.Keys.Should().HaveCount(2);
        envelope.Keys["OrderId"].GetInt32().Should().Be(1);
        envelope.Keys["LineNumber"].GetInt32().Should().Be(2);
    }

    [Fact]
    public void Extended_payload_update_with_previous_values_reports_only_changed_watched_columns()
    {
        var config = CreateConfiguration(ExtendedNotificationPayloadBuilder.Instance, watchedUpdateColumns: ["Status", "Total"]);
        var json = NotificationPayloadJsonMaterializer.Materialize(
            config,
            NotificationOperation.Update,
            currentValues: new Dictionary<string, object?> { ["Id"] = 1, ["Status"] = "Shipped", ["Total"] = 10m },
            previousValues: new Dictionary<string, object?> { ["Id"] = 1, ["Status"] = "New", ["Total"] = 10m },
            timestamp: DateTimeOffset.UtcNow);

        var envelope = DefaultNotificationPayloadDeserializer.Instance.Deserialize("orders", json);

        envelope.Changed.Should().Equal("Status");
    }

    [Fact]
    public void Extended_payload_update_without_previous_values_reports_no_changed_columns()
    {
        // No REPLICA IDENTITY FULL -> no old row -> nothing to compare against, same as an
        // unconditional trigger update reporting nothing changed because no comparison happened.
        var config = CreateConfiguration(ExtendedNotificationPayloadBuilder.Instance, watchedUpdateColumns: ["Status"]);
        var json = NotificationPayloadJsonMaterializer.Materialize(
            config,
            NotificationOperation.Update,
            currentValues: new Dictionary<string, object?> { ["Id"] = 1, ["Status"] = "Shipped" },
            previousValues: null,
            timestamp: DateTimeOffset.UtcNow);

        var envelope = DefaultNotificationPayloadDeserializer.Instance.Deserialize("orders", json);

        envelope.Changed.Should().BeEmpty();
    }

    [Fact]
    public void Delete_uses_currentValues_as_the_deleted_rows_key_source()
    {
        // For a delete, callers pass the old/key values from the replication stream as
        // currentValues -- there is no "NEW" row for a delete to draw from.
        var config = CreateConfiguration();
        var json = NotificationPayloadJsonMaterializer.Materialize(
            config,
            NotificationOperation.Delete,
            currentValues: new Dictionary<string, object?> { ["Id"] = 7 },
            previousValues: null,
            timestamp: DateTimeOffset.UtcNow);

        var envelope = DefaultNotificationPayloadDeserializer.Instance.Deserialize("orders", json);

        envelope.Operation.Should().Be(NotificationOperation.Delete);
        envelope.Keys["id"].GetInt32().Should().Be(7);
    }

    [Fact]
    public void Timestamp_field_round_trips_through_the_deserializer()
    {
        var config = CreateConfiguration(ExtendedNotificationPayloadBuilder.Instance);
        var timestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var json = NotificationPayloadJsonMaterializer.Materialize(
            config,
            NotificationOperation.Insert,
            currentValues: new Dictionary<string, object?> { ["Id"] = 1 },
            previousValues: null,
            timestamp: timestamp);

        var envelope = DefaultNotificationPayloadDeserializer.Instance.Deserialize("orders", json);

        envelope.Timestamp.Should().Be(timestamp);
    }

    [Fact]
    public void Unconditional_update_reports_no_changed_columns_even_with_previous_values()
    {
        var config = CreateConfiguration(ExtendedNotificationPayloadBuilder.Instance, unconditionalUpdate: true);
        var json = NotificationPayloadJsonMaterializer.Materialize(
            config,
            NotificationOperation.Update,
            currentValues: new Dictionary<string, object?> { ["Id"] = 1, ["Status"] = "Shipped" },
            previousValues: new Dictionary<string, object?> { ["Id"] = 1, ["Status"] = "New" },
            timestamp: DateTimeOffset.UtcNow);

        var envelope = DefaultNotificationPayloadDeserializer.Instance.Deserialize("orders", json);

        envelope.Changed.Should().BeEmpty();
    }

    [Fact]
    public void Missing_column_value_materializes_as_json_null_rather_than_throwing()
    {
        // A projected/extended payload can reference a non-key column that a KeyDeleteMessage (no
        // REPLICA IDENTITY FULL) simply never carries for a delete.
        var config = CreateConfiguration(ExtendedNotificationPayloadBuilder.Instance);
        var act = () => NotificationPayloadJsonMaterializer.Materialize(
            config,
            NotificationOperation.Delete,
            currentValues: new Dictionary<string, object?> { ["Id"] = 1 },
            previousValues: null,
            timestamp: DateTimeOffset.UtcNow);

        act.Should().NotThrow();
    }
}
