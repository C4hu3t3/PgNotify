using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Orders.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Order",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustomerName = table.Column<string>(type: "text", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Order", x => x.Id);
                })
                .Annotation("Notifications:Fingerprint", "ops=All;watched=;prefix=;InsertChannel=Order;UpdateChannel=Order;DeleteChannel=Order;sql=5248ca94ea4446439c33d04e05724d5a47ff0d41ffdceefe96849637b6c722a1")
                .Annotation("Notifications:NamePrefix", "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Order")
                .Annotation("Notifications:Fingerprint", "ops=All;watched=;prefix=;InsertChannel=Order;UpdateChannel=Order;DeleteChannel=Order;sql=5248ca94ea4446439c33d04e05724d5a47ff0d41ffdceefe96849637b6c722a1")
                .Annotation("Notifications:NamePrefix", "");
        }
    }
}
