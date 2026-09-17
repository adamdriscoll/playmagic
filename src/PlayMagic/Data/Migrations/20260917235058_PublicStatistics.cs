using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PlayMagic.Data.Migrations
{
    /// <inheritdoc />
    public partial class PublicStatistics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PublicStatistics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GamesCreated = table.Column<long>(type: "INTEGER", nullable: false),
                    GamesPlayed = table.Column<long>(type: "INTEGER", nullable: false),
                    CommanderGamesPlayed = table.Column<long>(type: "INTEGER", nullable: false),
                    RegularGamesPlayed = table.Column<long>(type: "INTEGER", nullable: false),
                    PlayersJoined = table.Column<long>(type: "INTEGER", nullable: false),
                    CardsLoaded = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PublicStatistics", x => x.Id);
                });

            // Seed from records still retained at upgrade time. Later cleanup only
            // removes game records; these cumulative totals remain untouched.
            migrationBuilder.Sql("""
                INSERT INTO "PublicStatistics"
                    ("Id", "GamesCreated", "GamesPlayed", "CommanderGamesPlayed", "RegularGamesPlayed", "PlayersJoined", "CardsLoaded")
                SELECT 1,
                    (SELECT COUNT(*) FROM "Games"),
                    (SELECT COUNT(*) FROM (SELECT "GameId" FROM "Players" GROUP BY "GameId" HAVING COUNT(*) >= 2)),
                    (SELECT COUNT(*) FROM "Games" AS g WHERE g."Format" = 'Commander'
                        AND (SELECT COUNT(*) FROM "Players" AS p WHERE p."GameId" = g."Id") >= 2),
                    (SELECT COUNT(*) FROM "Games" AS g WHERE g."Format" = 'Regular'
                        AND (SELECT COUNT(*) FROM "Players" AS p WHERE p."GameId" = g."Id") >= 2),
                    (SELECT COUNT(*) FROM "Players"),
                    (SELECT COUNT(*) FROM "GameCards");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PublicStatistics");
        }
    }
}
