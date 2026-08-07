using Microsoft.EntityFrameworkCore;
using PgNotify.Migrations.Tests.TestModels;

namespace PgNotify.Migrations.Tests;

/// <summary>
/// Inheritance decides which table a trigger can belong to, and the answer differs per strategy.
/// Measured before the validation existed: a table-per-hierarchy derived type produced no trigger
/// and no error at all, while the root and a table-per-type derived type both produced one.
/// </summary>
public class NotificationInheritanceTests
{
    private abstract class Animal
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";
    }

    private sealed class Dog : Animal
    {
        public string Breed { get; set; } = "";
    }

    [Fact]
    public void Configuring_a_table_per_hierarchy_derived_type_is_refused()
    {
        var act = () => MigrationTestHelper.GenerateCreateSql(b =>
        {
            b.Entity<Animal>().ToTable("Animals");
            b.Entity<Dog>().HasDatabaseNotifications(o => o.OnInsert());
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Dog*Animals*Animal*table-per-hierarchy*");
    }

    [Fact]
    public void Configuring_the_root_of_a_hierarchy_generates_one_trigger_for_the_whole_table()
    {
        var sql = string.Join("\n", MigrationTestHelper.GenerateCreateSql(b =>
        {
            b.Entity<Animal>(e =>
            {
                e.ToTable("Animals");
                e.HasDatabaseNotifications(o => o.OnInsert());
            });
            b.Entity<Dog>();
        }));

        sql.Should().Contain("CREATE TRIGGER \"trg_Animals_notify\"");

        // The consequence worth documenting: a Dog row notifies under the root's name, because the
        // trigger cannot tell the discriminator apart from any other column.
        sql.Should().Contain("'entity', 'Animal'::text");
    }

    [Fact]
    public void Configuring_a_derived_type_with_a_table_of_its_own_is_allowed()
    {
        // Table-per-type: nothing is shared, so the trigger has an unambiguous owner.
        var sql = string.Join("\n", MigrationTestHelper.GenerateCreateSql(b =>
        {
            b.Entity<Animal>().ToTable("Animals");
            b.Entity<Dog>().ToTable("Dogs").HasDatabaseNotifications(o => o.OnInsert());
        }));

        sql.Should().Contain("CREATE TRIGGER \"trg_Dogs_notify\"");
        sql.Should().Contain("'entity', 'Dog'::text");
        sql.Should().NotContain("trg_Animals_notify");
    }

    [Fact]
    public void An_entity_with_no_inheritance_at_all_is_unaffected()
    {
        var sql = string.Join("\n", MigrationTestHelper.GenerateCreateSql(b =>
            b.Entity<Product>().HasDatabaseNotifications(o => o.OnInsert())));

        sql.Should().Contain("CREATE TRIGGER");
    }
}
