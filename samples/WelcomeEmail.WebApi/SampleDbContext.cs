using Microsoft.EntityFrameworkCore;
using PgNotify;
using WelcomeEmail.Model;

namespace WelcomeEmail.WebApi;

public class SampleDbContext(DbContextOptions<SampleDbContext> options) : DbContext(options)
{
    public DbSet<Member> Members => Set<Member>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Member>(entity =>
        {
            // OnInsert() only: a member row is written once, at sign-up, and that is the single
            // event the mailer cares about. No OnUpdate/OnDelete means no trigger branch for
            // them and nothing on the wire when a profile is edited or an account removed.
            //
            // WithPayload(...) projects Email and DisplayName straight off NEW in the trigger, so
            // the notification carries them alongside the always-included key. That is what lets
            // WelcomeEmail.Mailer be a process with zero database access: it renders the email
            // entirely from the payload (envelope.ToTyped<NewMember>()), never re-reading the row.
            entity.HasDatabaseNotifications(o => o
                .OnInsert()
                .WithPayload(m => new { m.Email, m.DisplayName }));
        });
    }
}
