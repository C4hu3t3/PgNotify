using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CacheInvalidation.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Price = table.Column<decimal>(type: "numeric", nullable: false),
                    IsAvailable = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                })
                .Annotation("Notifications:Fingerprint", "ops=All;watched=;prefix=;InsertChannel=Products;UpdateChannel=Products;DeleteChannel=Products;sql=aca129772e3cf5729fac1a1e273f4ac2582fb04d79529953aa3076a79cf896b6")
                .Annotation("Notifications:NamePrefix", "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Products")
                .Annotation("Notifications:Fingerprint", "ops=All;watched=;prefix=;InsertChannel=Products;UpdateChannel=Products;DeleteChannel=Products;sql=aca129772e3cf5729fac1a1e273f4ac2582fb04d79529953aa3076a79cf896b6")
                .Annotation("Notifications:NamePrefix", "");
        }
    }
}
