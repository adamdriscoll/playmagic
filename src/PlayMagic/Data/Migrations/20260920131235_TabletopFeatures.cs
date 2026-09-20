using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlayMagic.Data.Migrations
{
    /// <inheritdoc />
    public partial class TabletopFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CommanderDamageJson",
                table: "Players",
                type: "TEXT",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TransferExpiresUtc",
                table: "Players",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TransferTokenHash",
                table: "Players",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActivePlayerId",
                table: "Games",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TurnNumber",
                table: "Games",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.Sql("""
                UPDATE "Games"
                SET "ActivePlayerId" = (
                    SELECT "Id" FROM "Players"
                    WHERE "Players"."GameId" = "Games"."Id"
                    ORDER BY "JoinedUtc"
                    LIMIT 1
                );
                """);

            migrationBuilder.CreateTable(
                name: "GameEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GameId = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<string>(type: "TEXT", nullable: true),
                    PlayerName = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameEvents_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GameEvents_GameId_Id",
                table: "GameEvents",
                columns: new[] { "GameId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GameEvents");

            migrationBuilder.DropColumn(
                name: "CommanderDamageJson",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "TransferExpiresUtc",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "TransferTokenHash",
                table: "Players");

            migrationBuilder.DropColumn(
                name: "ActivePlayerId",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "TurnNumber",
                table: "Games");
        }
    }
}
