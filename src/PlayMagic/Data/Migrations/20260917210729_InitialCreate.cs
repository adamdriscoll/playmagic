using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlayMagic.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Cards",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OracleId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TypeLine = table.Column<string>(type: "TEXT", nullable: false),
                    ManaCost = table.Column<string>(type: "TEXT", nullable: false),
                    OracleText = table.Column<string>(type: "TEXT", nullable: false),
                    ImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    BackImageUrl = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CatalogSyncs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LastUpdatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CardCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogSyncs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Games",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Format = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Games", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Players",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    GameId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    DeckName = table.Column<string>(type: "TEXT", nullable: false),
                    DeckUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Life = table.Column<int>(type: "INTEGER", nullable: false),
                    CountersJson = table.Column<string>(type: "TEXT", nullable: false),
                    JoinedUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Players", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Players_Games_GameId",
                        column: x => x.GameId,
                        principalTable: "Games",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GameCards",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PlayerId = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    ImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    BackImageUrl = table.Column<string>(type: "TEXT", nullable: true),
                    TypeLine = table.Column<string>(type: "TEXT", nullable: false),
                    OracleText = table.Column<string>(type: "TEXT", nullable: false),
                    Zone = table.Column<string>(type: "TEXT", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Tapped = table.Column<bool>(type: "INTEGER", nullable: false),
                    CountersJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GameCards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GameCards_Players_PlayerId",
                        column: x => x.PlayerId,
                        principalTable: "Players",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Cards_Name",
                table: "Cards",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Cards_OracleId",
                table: "Cards",
                column: "OracleId");

            migrationBuilder.CreateIndex(
                name: "IX_GameCards_PlayerId_Zone_SortOrder",
                table: "GameCards",
                columns: new[] { "PlayerId", "Zone", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Players_GameId_TokenHash",
                table: "Players",
                columns: new[] { "GameId", "TokenHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Cards");

            migrationBuilder.DropTable(
                name: "CatalogSyncs");

            migrationBuilder.DropTable(
                name: "GameCards");

            migrationBuilder.DropTable(
                name: "Players");

            migrationBuilder.DropTable(
                name: "Games");
        }
    }
}
