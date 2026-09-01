using System.ComponentModel.DataAnnotations.Schema;

namespace WelcomeEmail.Model;

/// <summary>
/// The one thing <c>WelcomeEmail.WebApi</c> (which maps this with EF Core and owns the migration)
/// and <c>WelcomeEmail.Mailer</c> (which never touches EF Core at all) share — a plain entity
/// class. A member signing up is one <c>INSERT</c>; the mailer sends exactly one welcome email per
/// such row.
/// </summary>
/// <remarks>
/// Notifications are configured fluently in <c>WelcomeEmail.WebApi</c>'s
/// <c>SampleDbContext.OnModelCreating</c> rather than with a <c>[NotifyChanges]</c> attribute here,
/// because this sample asks for a <b>projected</b> payload — <c>WithPayload(m =&gt; new { m.Email,
/// m.DisplayName })</c> — and only the fluent API can declare a payload shape. The consequence
/// worth seeing: the mailer needs no database access, because the notification itself carries
/// everything the email template renders (see <c>WelcomeEmail.Mailer/NewMember.cs</c>).
/// <para>
/// <c>[Table("Member")]</c> is here for a cross-process reason, exactly as in
/// <c>TaskBoard.Model.TaskItem</c>: the mailer has no model to read, so it names its channel
/// itself (<c>MapChannel&lt;Member&gt;("Member")</c>), and the per-entity channel is the table
/// name. Pinning the table in the shared project keeps that name from being decided by a
/// <c>DbSet</c> property the mailer never sees.
/// </para>
/// </remarks>
[Table("Member")]
public class Member
{
    public int Id { get; set; }

    public string Email { get; set; } = "";

    public string DisplayName { get; set; } = "";
}
