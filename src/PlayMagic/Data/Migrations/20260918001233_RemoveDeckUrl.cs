using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlayMagic.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDeckUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeckUrl",
                table: "Players");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeckUrl",
                table: "Players",
                type: "TEXT",
                nullable: true);
        }
    }
}
