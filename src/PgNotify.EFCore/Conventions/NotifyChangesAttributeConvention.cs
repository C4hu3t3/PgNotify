using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using PgNotify.Diagnostics;
using PgNotify.Internal;
using PgNotify.Metadata;
using PgNotify.Naming;
using PgNotify.Payloads;

namespace PgNotify.Conventions;

/// <summary>
/// Discovers <see cref="NotifyChangesAttribute"/> on newly added entity types and applies the
/// same annotations the fluent <c>HasDatabaseNotifications()</c> API would, so both
/// configuration styles converge on identical model state (see <see cref="NotificationConfigurationWriter"/>).
/// </summary>
/// <remarks>
/// Applied with <c>fromDataAnnotation: true</c>, so an explicit fluent
/// <c>HasDatabaseNotifications()</c> call for the same entity always takes precedence — EF Core's
/// convention system resolves conflicting annotation sources in favor of explicit configuration.
/// </remarks>
public sealed class NotifyChangesAttributeConvention(IDiagnosticsLogger<DbLoggerCategory.Model.Validation> logger)
    : IEntityTypeAddedConvention
{
    /// <inheritdoc />
    public void ProcessEntityTypeAdded(
        IConventionEntityTypeBuilder entityTypeBuilder,
        IConventionContext<IConventionEntityTypeBuilder> context)
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);

        var clrType = entityTypeBuilder.Metadata.ClrType;
        var attribute = clrType.GetCustomAttributes(typeof(NotifyChangesAttribute), inherit: false)
            .Cast<NotifyChangesAttribute>()
            .FirstOrDefault();

        if (attribute is null)
        {
            return;
        }

        var watchedUpdateProperties = attribute.WatchedProperties ?? [];
        if (!attribute.CompareColumnsOnUpdate && watchedUpdateProperties.Length > 0)
        {
            // Unlike the channel-strategy/payload-builder conflicts below, there's a safe default
            // to fall back to instead of rejecting the model outright: CompareColumnsOnUpdate =
            // false already means "watch no columns" on its own, so WatchedProperties here can only
            // be redundant with it or contradict it - either way, honoring the false and ignoring
            // WatchedProperties is a defensible reading, so this is a warning to clean up, not a
            // fatal error.
            logger.ConflictingCompareColumnsAndWatchedPropertiesWarning(clrType.Name);
            watchedUpdateProperties = [];
        }

        var (channelStrategyKind, channelStrategyArgument) = ResolveChannelStrategy(attribute, clrType);
        var (payloadBuilderKind, payloadBuilderTypeName) = ResolvePayloadBuilder(attribute, clrType);

        NotificationConfigurationWriter.Apply(
            (name, value) => entityTypeBuilder.HasAnnotation(name, value, fromDataAnnotation: true),
            attribute.Operations,
            watchedUpdateProperties: watchedUpdateProperties,
            channelStrategyKind,
            channelStrategyArgument,
            channelNameOverride: attribute.ChannelName,
            payloadBuilderKind,
            payloadBuilderTypeName,
            namePrefix: attribute.NamePrefix,
            payloadOverflow: attribute.PayloadOverflow,
            payloadProperties: attribute.PayloadProperties ?? [],
            unconditionalUpdate: !attribute.CompareColumnsOnUpdate,
            deliveryMode: attribute.Delivery,
            replicaIdentityFull: attribute.ReplicaIdentityFull,
            replicationConsumerGroup: attribute.ReplicationConsumerGroup);
    }

    private static (string Kind, string? Argument) ResolveChannelStrategy(NotifyChangesAttribute attribute, Type clrType)
    {
        if (attribute.ChannelStrategyType is not { } customType)
        {
            return attribute.ChannelStrategy switch
            {
                NotificationChannelStrategy.Single =>
                    (NotificationChannelStrategyKind.Single, attribute.ChannelArgument ?? SingleChannelNamingStrategy.DefaultChannelName),
                NotificationChannelStrategy.Topic =>
                    (NotificationChannelStrategyKind.Topic, attribute.ChannelArgument ?? "."),
                _ => (NotificationChannelStrategyKind.PerEntity, null),
            };
        }

        // Two strategies configured at once has no defensible reading, and picking one would leave
        // the ignored half looking applied.
        return attribute.ChannelStrategy != NotificationChannelStrategy.PerEntity
            ? throw new InvalidOperationException(
                $"[NotifyChanges] on '{clrType.Name}' sets both ChannelStrategy and ChannelStrategyType. Set one of them.")
            : !typeof(INotificationChannelNamingStrategy).IsAssignableFrom(customType)
            ? throw new InvalidOperationException(
                $"[NotifyChanges] on '{clrType.Name}' sets ChannelStrategyType to '{customType}', which does not implement "
                + $"{nameof(INotificationChannelNamingStrategy)}.")
            : ((string Kind, string? Argument))(NotificationChannelStrategyKind.Custom, NotificationCustomTypes.RequireParameterlessConstructor(customType));
    }

    private static (string Kind, string? TypeName) ResolvePayloadBuilder(NotifyChangesAttribute attribute, Type clrType)
    {
        if (attribute.PayloadBuilder is { } customType)
        {
            return attribute.PayloadProperties is { Length: > 0 }
                ? throw new InvalidOperationException(
                    $"[NotifyChanges] on '{clrType.Name}' sets both PayloadBuilder and PayloadProperties. Set one of them.")
                : !typeof(INotificationPayloadBuilder).IsAssignableFrom(customType)
                ? throw new InvalidOperationException(
                    $"[NotifyChanges] on '{clrType.Name}' sets PayloadBuilder to '{customType}', which does not implement "
                    + $"{nameof(INotificationPayloadBuilder)}.")
                : ((string Kind, string? TypeName))(NotificationPayloadBuilderKind.Custom, NotificationCustomTypes.RequireParameterlessConstructor(customType));
        }

        if (attribute.PayloadProperties is { Length: > 0 })
        {
            return (NotificationPayloadBuilderKind.Projected, null);
        }

        return attribute.Payload == NotificationPayloadKind.Extended
            ? (NotificationPayloadBuilderKind.Extended, null)
            : (NotificationPayloadBuilderKind.Minimal, null);
    }
}
