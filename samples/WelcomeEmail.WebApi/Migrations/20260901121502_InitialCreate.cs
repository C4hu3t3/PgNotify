using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace WelcomeEmail.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Member",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Email = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Member", x => x.Id);
                })
                .Annotation("Notifications:Fingerprint", "ops=Insert;watched=;prefix=;InsertChannel=Member;sql=0121a8f658ed8449ee4cb10c7e0d358baee39f41a8c17f9f50f7abee199c44d0")
                .Annotation("Notifications:NamePrefix", "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Member")
                .Annotation("Notifications:Fingerprint", "ops=Insert;watched=;prefix=;InsertChannel=Member;sql=0121a8f658ed8449ee4cb10c7e0d358baee39f41a8c17f9f50f7abee199c44d0")
                .Annotation("Notifications:NamePrefix", "");
        }
    }
}
