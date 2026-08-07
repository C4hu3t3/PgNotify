using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using PgNotify;
using PgNotify.Naming;

namespace PgNotify.IntegrationTests.TestModels;

/// <summary>
/// Configured entirely by attribute, using the options that only existed fluently until now: a
/// watched-property filter, a projected payload, and the topic channel strategy.
/// </summary>
[Table("attribute_tickets")]
[NotifyChanges(
    NotificationOperations.Update,
    WatchedProperties = [nameof(Status)],
    PayloadProperties = [nameof(Status), nameof(Title)],
    ChannelStrategy = NotificationChannelStrategy.Topic,
    ChannelArgument = "-")]
public class AttributeTicket
{
    public int Id { get; set; }

    public string Title { get; set; } = "";

    public string Status { get; set; } = "";

    public string InternalNote { get; set; } = "";
}

public sealed class AttributeTicketDbContext(DbContextOptions<AttributeTicketDbContext> options) : DbContext(options)
{
    public DbSet<AttributeTicket> Tickets => Set<AttributeTicket>();
}
