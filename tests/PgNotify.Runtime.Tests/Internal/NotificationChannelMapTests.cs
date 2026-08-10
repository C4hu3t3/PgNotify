using PgNotify.Internal;
using PgNotify.Runtime.Tests.TestModels;
using PgNotify.Serialization;

namespace PgNotify.Runtime.Tests.Internal;

public class NotificationChannelMapTests
{
    private static NotificationEnvelope Envelope(string channel, string entity, NotificationOperation operation = NotificationOperation.Update) => new()
    {
        Channel = channel,
        Entity = entity,
        Operation = operation,
        Keys = new Dictionary<string, System.Text.Json.JsonElement>(),
        RawPayload = "{}",
    };

    [Fact]
    public void A_mapped_channel_routes_every_operation_to_the_same_entity()
    {
        var map = new NotificationChannelMap();
        map.MapChannel("users", typeof(TestUser));

        map.Find(Envelope("users", "TestUser", NotificationOperation.Insert))!.EntityType.Should().Be(typeof(TestUser));
        map.Find(Envelope("users", "TestUser", NotificationOperation.Update))!.EntityType.Should().Be(typeof(TestUser));
        map.Find(Envelope("users", "TestUser", NotificationOperation.Delete))!.EntityType.Should().Be(typeof(TestUser));
    }

    [Fact]
    public void One_entity_can_be_mapped_to_several_channels()
    {
        // What the topic naming strategy produces: user.created / user.updated / user.deleted.
        var map = new NotificationChannelMap();
        map.MapChannel("user.created", typeof(TestUser));
        map.MapChannel("user.updated", typeof(TestUser));

        map.Find(Envelope("user.created", "TestUser", NotificationOperation.Insert))!.EntityType.Should().Be(typeof(TestUser));
        map.Find(Envelope("user.updated", "TestUser"))!.EntityType.Should().Be(typeof(TestUser));
        map.Channels.Should().BeEquivalentTo(["user.created", "user.updated"]);
    }

    [Fact]
    public void A_shared_channel_is_disambiguated_by_the_payloads_entity_name()
    {
        // What WithSingleChannel produces: several entities, one channel.
        var map = new NotificationChannelMap();
        map.MapChannel("app_events", typeof(TestUser));
        map.MapChannel("app_events", typeof(TestOrder));

        map.Find(Envelope("app_events", "TestUser"))!.EntityType.Should().Be(typeof(TestUser));
        map.Find(Envelope("app_events", "TestOrder"))!.EntityType.Should().Be(typeof(TestOrder));
        map.Channels.Should().ContainSingle();
    }

    [Fact]
    public void An_entity_name_arriving_on_a_channel_it_was_not_mapped_to_finds_nothing()
    {
        var map = new NotificationChannelMap();
        map.MapChannel("users", typeof(TestUser));

        map.Find(Envelope("orders", "TestUser")).Should().BeNull();
        map.Find(Envelope("users", "TestOrder")).Should().BeNull();
    }

    [Fact]
    public void A_channel_mapped_without_an_entity_is_listened_on_but_routes_to_no_entity()
    {
        var map = new NotificationChannelMap();
        map.MapChannel("legacy_audit");

        map.Channels.Should().BeEquivalentTo(["legacy_audit"]);
        map.Find(Envelope("legacy_audit", "AuditEvent")).Should().BeNull();
    }

    [Fact]
    public void Mapping_the_same_pair_twice_is_a_no_op()
    {
        var map = new NotificationChannelMap();
        map.MapChannel("users", typeof(TestUser));

        var act = () => map.MapChannel("users", typeof(TestUser));

        act.Should().NotThrow();
        map.Channels.Should().ContainSingle();
    }

    [Fact]
    public void Two_types_sharing_a_name_on_one_channel_are_rejected_where_they_are_declared()
    {
        // The payload's "entity" field carries the short name only, so nothing arriving at runtime
        // could tell these apart - the collision has to be refused while the map is being built.
        var map = new NotificationChannelMap();
        map.MapChannel("app_events", typeof(TestUser));

        var act = () => map.MapChannel("app_events", typeof(PgNotify.Runtime.Tests.TestModels.Duplicates.TestUser));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*app_events*")
            .WithMessage($"*{typeof(PgNotify.Runtime.Tests.TestModels.Duplicates.TestUser).FullName}*");
    }

    [Fact]
    public void The_same_name_on_two_different_channels_is_not_a_collision()
    {
        var map = new NotificationChannelMap();
        map.MapChannel("users", typeof(TestUser));

        var act = () => map.MapChannel("other_users", typeof(PgNotify.Runtime.Tests.TestModels.Duplicates.TestUser));

        act.Should().NotThrow();
        map.Find(Envelope("users", "TestUser"))!.EntityType.Should().Be(typeof(TestUser));
        map.Find(Envelope("other_users", "TestUser"))!.EntityType.Should().Be(typeof(PgNotify.Runtime.Tests.TestModels.Duplicates.TestUser));
    }

    [Fact]
    public void MappedEntityTypes_reflects_every_type_bound_to_a_channel_without_duplicates()
    {
        var map = new NotificationChannelMap();
        map.MapChannel("users", typeof(TestUser));
        map.MapChannel("user.created", typeof(TestUser));
        map.MapChannel("orders", typeof(TestOrder));
        map.MapChannel("legacy_audit");

        map.MappedEntityTypes.Should().BeEquivalentTo([typeof(TestUser), typeof(TestOrder)]);
    }

    [Fact]
    public void MappedEntityNames_reflects_every_entity_name_bound_to_a_channel_without_duplicates()
    {
        var map = new NotificationChannelMap();
        map.MapChannel("users", typeof(TestUser));
        map.MapChannel("user.created", typeof(TestUser));
        map.MapChannel("orders", typeof(TestOrder));
        map.MapChannel("legacy_audit");

        map.MappedEntityNames.Should().BeEquivalentTo(["TestUser", "TestOrder"]);
    }

    [Fact]
    public void MappingResolved_is_false_until_explicitly_marked()
    {
        var map = new NotificationChannelMap();
        map.MapChannel("users", typeof(TestUser));

        map.MappingResolved.Should().BeFalse();

        map.MarkMappingResolved();

        map.MappingResolved.Should().BeTrue();
    }
}
