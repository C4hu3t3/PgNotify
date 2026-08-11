using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PgNotify.Internal;
using PgNotify.Metadata;
using PgNotify.Naming;
using PgNotify.Payloads;

namespace PgNotify;

/// <summary>
/// Configures PostgreSQL database notifications for an entity, via
/// <c>EntityTypeBuilder&lt;TEntity&gt;.HasDatabaseNotifications(options =&gt; ...)</c>.
/// </summary>
public sealed class NotificationOptionsBuilder<TEntity>
    where TEntity : class
{
    private NotificationOperations _operations = NotificationOperations.None;
    private readonly List<string> _watchedUpdateProperties = [];
    private bool _unconditionalUpdate;
    private string _channelStrategyKind = NotificationChannelStrategyKind.PerEntity;
    private string? _channelStrategyArgument;
    private string? _channelNameOverride;
    private string _payloadBuilderKind = NotificationPayloadBuilderKind.Minimal;
    private string? _payloadBuilderTypeName;
    private string? _namePrefix;
    private NotificationPayloadOverflow _payloadOverflow = NotificationPayloadOverflow.Truncate;
    private readonly List<string> _payloadProperties = [];

    internal NotificationOptionsBuilder()
    {
    }

    /// <summary>Raise a notification after a row is inserted.</summary>
    public NotificationOptionsBuilder<TEntity> OnInsert()
    {
        _operations |= NotificationOperations.Insert;
        return this;
    }

    /// <summary>Raise a notification after a row is deleted.</summary>
    public NotificationOptionsBuilder<TEntity> OnDelete()
    {
        _operations |= NotificationOperations.Delete;
        return this;
    }

    /// <summary>
    /// Raise a notification after a row is updated. If <paramref name="watchedProperties"/> is
    /// given (e.g. <c>x =&gt; new { x.Name, x.Email }</c>), the trigger only fires when one of
    /// those specific properties actually changed (compared with <c>IS DISTINCT FROM</c>);
    /// otherwise any mapped column change fires it. See the <see cref="OnUpdate(bool)"/> overload
    /// to raise a notification for every <c>UPDATE</c>, including one that changes no value.
    /// </summary>
    public NotificationOptionsBuilder<TEntity> OnUpdate(Expression<Func<TEntity, object?>>? watchedProperties = null)
    {
        _operations |= NotificationOperations.Update;
        if (watchedProperties is not null)
        {
            _unconditionalUpdate = false;
            _watchedUpdateProperties.AddRange(PropertySelectorExpressionHelper.GetPropertyNames(watchedProperties));
        }

        return this;
    }

    /// <summary>
    /// Raise a notification after a row is updated. <paramref name="compareColumns"/> =
    /// <see langword="true"/> is the same as the bare <c>OnUpdate()</c> call: the
    /// trigger only fires when at least one mapped column actually changed value (compared with
    /// <c>IS DISTINCT FROM</c>). <paramref name="compareColumns"/> = <see langword="false"/> skips
    /// that comparison entirely — every <c>UPDATE</c> statement that touches the row raises a
    /// notification, including one that changes no value.
    /// </summary>
    public NotificationOptionsBuilder<TEntity> OnUpdate(bool compareColumns)
    {
        _operations |= NotificationOperations.Update;
        _unconditionalUpdate = !compareColumns;
        return this;
    }

    /// <summary>
    /// Raise a notification for every operation — the same as calling <see cref="OnInsert"/>,
    /// <see cref="OnUpdate(Expression{Func{TEntity, object}})"/> and <see cref="OnDelete"/>
    /// together. <paramref name="watchedProperties"/> has the same meaning as on
    /// <see cref="OnUpdate(Expression{Func{TEntity, object}})"/> and only affects the update
    /// branch: insert and delete have no "before" row for a property selector to compare against.
    /// </summary>
    public NotificationOptionsBuilder<TEntity> OnAny(Expression<Func<TEntity, object?>>? watchedProperties = null)
    {
        OnInsert();
        OnUpdate(watchedProperties);
        OnDelete();
        return this;
    }

    /// <summary>
    /// Raise a notification for every operation — the same as calling <see cref="OnInsert"/>,
    /// <see cref="OnUpdate(bool)"/> and <see cref="OnDelete"/> together.
    /// <paramref name="compareColumns"/> has the same meaning as on <see cref="OnUpdate(bool)"/>
    /// and only affects the update branch: insert and delete have no "before" row to compare
    /// against, so there is nothing for it to change there.
    /// </summary>
    public NotificationOptionsBuilder<TEntity> OnAny(bool compareColumns)
    {
        OnInsert();
        OnUpdate(compareColumns);
        OnDelete();
        return this;
    }

    /// <summary>Uses a custom channel-naming strategy type, constructed with its public parameterless constructor.</summary>
    public NotificationOptionsBuilder<TEntity> WithChannelStrategy<TStrategy>()
        where TStrategy : INotificationChannelNamingStrategy, new()
    {
        _channelStrategyKind = NotificationChannelStrategyKind.Custom;
        _channelStrategyArgument = typeof(TStrategy).AssemblyQualifiedName;
        return this;
    }

    /// <summary>One channel per entity, named after the mapped table (e.g. <c>users</c>). This is the default.</summary>
    public NotificationOptionsBuilder<TEntity> WithPerEntityChannel()
    {
        _channelStrategyKind = NotificationChannelStrategyKind.PerEntity;
        _channelStrategyArgument = null;
        return this;
    }

    /// <summary>One shared channel for every entity and operation.</summary>
    public NotificationOptionsBuilder<TEntity> WithSingleChannel(string channelName = Naming.SingleChannelNamingStrategy.DefaultChannelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        _channelStrategyKind = NotificationChannelStrategyKind.Single;
        _channelStrategyArgument = channelName;
        return this;
    }

    /// <summary>One channel per entity/operation pair, dot-separated (e.g. <c>user.created</c>).</summary>
    public NotificationOptionsBuilder<TEntity> WithTopicChannel(string separator = ".")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(separator);
        _channelStrategyKind = NotificationChannelStrategyKind.Topic;
        _channelStrategyArgument = separator;
        return this;
    }

    /// <summary>Overrides the channel name for every operation, bypassing the configured strategy.</summary>
    public NotificationOptionsBuilder<TEntity> WithChannelName(string channelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelName);
        _channelNameOverride = channelName;
        return this;
    }

    /// <summary>
    /// Emits exactly the selected properties (e.g. <c>x =&gt; new { x.Status, x.Total }</c>) into the
    /// payload, alongside the entity name, the operation, and the row's key — which is always
    /// included, whether or not the selector mentions it.
    /// </summary>
    /// <remarks>
    /// Declaring the payload's shape is what lets a typed event's shape be stated rather than
    /// inferred: <c>record OrderUpdated(int Id, string Status)</c> binds against
    /// <c>WithPayload(x =&gt; x.Status)</c> without anyone having to know which payload default the
    /// configuration style happened to pick.
    /// </remarks>
    /// <remarks>
    /// Only scalar mapped properties can be projected — the values are read straight off
    /// <c>NEW</c>/<c>OLD</c> in the trigger. Note that this puts row data in the notification
    /// payload, which is subject to <c>pg_notify</c>'s 7999-byte limit; see
    /// <see cref="WithPayloadOverflow"/> for what happens when a row is too big.
    /// </remarks>
    public NotificationOptionsBuilder<TEntity> WithPayload(Expression<Func<TEntity, object?>> payloadProperties)
    {
        ArgumentNullException.ThrowIfNull(payloadProperties);

        _payloadBuilderKind = NotificationPayloadBuilderKind.Projected;
        _payloadBuilderTypeName = null;
        _payloadProperties.Clear();
        _payloadProperties.AddRange(PropertySelectorExpressionHelper.GetPropertyNames(payloadProperties));

        return this;
    }

    /// <summary>Uses a custom payload builder type, constructed with its public parameterless constructor.</summary>
    public NotificationOptionsBuilder<TEntity> WithPayload<TBuilder>()
        where TBuilder : INotificationPayloadBuilder, new()
    {
        _payloadBuilderKind = NotificationPayloadBuilderKind.Custom;
        _payloadBuilderTypeName = typeof(TBuilder).AssemblyQualifiedName;
        return this;
    }

    /// <summary>
    /// Uses one of the two built-in payload shapes. <see cref="NotificationPayloadKind.Minimal"/> is
    /// the default: it is the smallest payload, the least row data leaving the database, and the one
    /// that puts the key where a typed event binds it — <c>id</c> at the top level rather than nested
    /// under <c>keys</c>. Ask for <see cref="NotificationPayloadKind.Extended"/> when a consumer needs
    /// the trigger's <c>timestamp</c> or the <c>changed</c> column list.
    /// </summary>
    public NotificationOptionsBuilder<TEntity> WithPayload(NotificationPayloadKind kind)
    {
        _payloadBuilderKind = kind == NotificationPayloadKind.Minimal
            ? NotificationPayloadBuilderKind.Minimal
            : NotificationPayloadBuilderKind.Extended;
        _payloadBuilderTypeName = null;
        return this;
    }

    /// <summary>Uses <see cref="MinimalNotificationPayloadBuilder"/>: <c>{"entity", "operation", "id"}</c>. This is the default.</summary>
    [Obsolete("Use WithPayload(NotificationPayloadKind.Minimal).")]
    public NotificationOptionsBuilder<TEntity> WithMinimalPayload() => WithPayload(NotificationPayloadKind.Minimal);

    /// <summary>Uses <see cref="ExtendedNotificationPayloadBuilder"/>: full entity/schema/table/operation/keys/changed/timestamp.</summary>
    [Obsolete("Use WithPayload(NotificationPayloadKind.Extended).")]
    public NotificationOptionsBuilder<TEntity> WithExtendedPayload() => WithPayload(NotificationPayloadKind.Extended);

    /// <summary>
    /// Prepends <paramref name="prefix"/> to the generated trigger/function names, so generated
    /// objects can be made unambiguous and collision-free against names you already use. Falls
    /// back to the model-wide default set via <c>modelBuilder.HasNotificationNamePrefix(...)</c>,
    /// or no prefix, when this is never called.
    /// </summary>
    public NotificationOptionsBuilder<TEntity> WithNamePrefix(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        _namePrefix = prefix;
        return this;
    }

    /// <summary>
    /// Chooses what the trigger does when the payload would exceed <c>pg_notify</c>'s 7999-byte
    /// limit. Defaults to <see cref="NotificationPayloadOverflow.Truncate"/>: overflowing raises
    /// inside the trigger, which aborts the write that produced the row, so failing is a
    /// deliberate choice rather than a default.
    /// </summary>
    public NotificationOptionsBuilder<TEntity> WithPayloadOverflow(NotificationPayloadOverflow overflow)
    {
        _payloadOverflow = overflow;
        return this;
    }

    internal void Save(EntityTypeBuilder<TEntity> entityTypeBuilder)
    {
        NotificationConfigurationWriter.Apply(
            (name, value) => entityTypeBuilder.Metadata.SetAnnotation(name, value),
            _operations,
            _watchedUpdateProperties,
            _channelStrategyKind,
            _channelStrategyArgument,
            _channelNameOverride,
            _payloadBuilderKind,
            _payloadBuilderTypeName,
            _namePrefix,
            _payloadOverflow,
            _payloadProperties,
            _unconditionalUpdate);
    }
}
