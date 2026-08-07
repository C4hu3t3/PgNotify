namespace PgNotify.Analyzers.Tests;

public class NotificationConfigurationAnalyzerTests
{
    private const string Preamble = """
        using Microsoft.EntityFrameworkCore;
        using PgNotify;
        using Microsoft.Extensions.DependencyInjection;

        namespace Sample;

        public class User
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public System.Collections.Generic.List<Order> Orders { get; set; } = new();
        }

        public class Order
        {
            public int Id { get; set; }
        }
        """;

    [Fact]
    public async Task PGN001_fires_when_entity_has_both_attribute_and_fluent_configuration()
    {
        var source = Preamble + """

            [NotifyChanges]
            public class AttributeUser
            {
                public int Id { get; set; }
            }

            public class SampleContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<AttributeUser>().HasDatabaseNotifications();
                }
            }
            """;

        var diagnostics = await CompilationTestHelper.GetDiagnosticsAsync(source);

        diagnostics.Should().ContainSingle(d => d.Id == "PGN001");
    }

    [Fact]
    public async Task PGN001_does_not_fire_for_fluent_only_configuration()
    {
        var source = Preamble + """

            public class SampleContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<User>().HasDatabaseNotifications();
                }
            }
            """;

        var diagnostics = await CompilationTestHelper.GetDiagnosticsAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "PGN001");
    }

    [Fact]
    public async Task PGN002_fires_when_OnUpdate_selects_a_collection_navigation()
    {
        var source = Preamble + """

            public class SampleContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<User>().HasDatabaseNotifications(o => o.OnUpdate(x => x.Orders));
                }
            }
            """;

        var diagnostics = await CompilationTestHelper.GetDiagnosticsAsync(source);

        diagnostics.Should().ContainSingle(d => d.Id == "PGN002");
    }

    [Fact]
    public async Task PGN002_does_not_fire_for_a_scalar_property()
    {
        var source = Preamble + """

            public class SampleContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<User>().HasDatabaseNotifications(o => o.OnUpdate(x => x.Name));
                }
            }
            """;

        var diagnostics = await CompilationTestHelper.GetDiagnosticsAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "PGN002");
    }

    [Fact]
    public async Task PGN004_fires_when_WithPayload_projects_a_collection_navigation()
    {
        var source = Preamble + """

            public class SampleContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<User>().HasDatabaseNotifications(o => o.WithPayload(x => x.Orders));
                }
            }
            """;

        var diagnostics = await CompilationTestHelper.GetDiagnosticsAsync(source);

        diagnostics.Should().ContainSingle(d => d.Id == "PGN004");
    }

    [Fact]
    public async Task PGN004_does_not_fire_for_projected_scalar_properties()
    {
        var source = Preamble + """

            public class SampleContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<User>().HasDatabaseNotifications(o => o.WithPayload(x => new { x.Id, x.Name }));
                }
            }
            """;

        var diagnostics = await CompilationTestHelper.GetDiagnosticsAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "PGN004");
    }

    [Fact]
    public async Task PGN003_fires_when_notifications_are_configured_but_the_runtime_is_never_registered()
    {
        var source = Preamble + """

            public class SampleContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<User>().HasDatabaseNotifications();
                }
            }
            """;

        var diagnostics = await CompilationTestHelper.GetDiagnosticsAsync(source);

        diagnostics.Should().ContainSingle(d => d.Id == "PGN003");
    }

    [Fact]
    public async Task PGN003_does_not_fire_when_AddPostgresNotifications_is_called()
    {
        var source = Preamble + """

            public class SampleContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<User>().HasDatabaseNotifications();
                }
            }

            public static class Startup
            {
                public static void Configure(IServiceCollection services)
                {
                    services.AddPostgresNotifications(o => o.ConnectionString = "Host=localhost");
                }
            }
            """;

        var diagnostics = await CompilationTestHelper.GetDiagnosticsAsync(source);

        diagnostics.Should().NotContain(d => d.Id == "PGN003");
    }

    [Fact]
    public async Task No_diagnostics_for_a_compilation_with_no_notification_usage_at_all()
    {
        var diagnostics = await CompilationTestHelper.GetDiagnosticsAsync(Preamble);

        diagnostics.Should().BeEmpty();
    }
}
