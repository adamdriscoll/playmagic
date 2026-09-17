using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlayMagic.Data.Migrations
{
    /// <inheritdoc />
    public partial class GameActivity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastActivityUtc",
                table: "Games",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Existing games have no activity history; give them a full grace period.
            migrationBuilder.Sql("UPDATE \"Games\" SET \"LastActivityUtc\" = CURRENT_TIMESTAMP");

            migrationBuilder.CreateIndex(
                name: "IX_Games_LastActivityUtc",
                table: "Games",
                column: "LastActivityUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Games_LastActivityUtc",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "LastActivityUtc",
                table: "Games");
        }
    }
}
