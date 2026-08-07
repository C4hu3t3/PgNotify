using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TaskBoard.WebApi.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskItem",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Title = table.Column<string>(type: "text", nullable: false),
                    IsDone = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskItem", x => x.Id);
                })
                .Annotation("Notifications:Fingerprint", "ops=All;watched=;prefix=;InsertChannel=TaskItem;UpdateChannel=TaskItem;DeleteChannel=TaskItem;payload=[entity:Constant::TaskItem][operation:Operation::][id:Column:Id:]")
                .Annotation("Notifications:NamePrefix", "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskItem")
                .Annotation("Notifications:Fingerprint", "ops=All;watched=;prefix=;InsertChannel=TaskItem;UpdateChannel=TaskItem;DeleteChannel=TaskItem;payload=[entity:Constant::TaskItem][operation:Operation::][id:Column:Id:]")
                .Annotation("Notifications:NamePrefix", "");
        }
    }
}
