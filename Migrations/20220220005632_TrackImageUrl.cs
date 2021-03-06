using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscordBoostRoleBot.Migrations
{
    public partial class TrackImageUrl : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImageUrl",
                table: "Roles",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageUrl",
                table: "Roles");
        }
    }
}
