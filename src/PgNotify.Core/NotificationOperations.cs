namespace PgNotify;

/// <summary>
/// The set of DML operations that can raise a database notification for an entity.
/// </summary>
[Flags]
public enum NotificationOperations
{
    /// <summary>No operations raise a notification.</summary>
    None = 0,

    /// <summary>Raise a notification after a row is inserted.</summary>
    Insert = 1,

    /// <summary>Raise a notification after a row is updated.</summary>
    Update = 2,

    /// <summary>Raise a notification after a row is deleted.</summary>
    Delete = 4,

    /// <summary>All operations raise a notification.</summary>
    All = Insert | Update | Delete,
}
