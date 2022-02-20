using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiscordBoostRoleBot.Migrations
{
    public partial class ImageHashTracking : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImageHash",
                table: "Roles",
                type: "TEXT",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageHash",
                table: "Roles");
        }
    }
}
