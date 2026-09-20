using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlayMagic.Data;
using PlayMagic.Services;

namespace PlayMagic.Tests;

[TestClass]
public sealed class PublicStatsTests
{
    [TestMethod]
    public async Task TotalsCountSuccessfulJoinsAndSurviveGameCleanup()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var provider = CreateServices(connection);
        var factory = provider.GetRequiredService<IDbContextFactory<PlayMagicDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.Cards.Add(new CatalogCard { Id = "island", OracleId = "island", Name = "Island" });
            db.CatalogSyncs.Add(new CatalogSync { Id = 1, CardCount = 1, LastUpdatedUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var games = provider.GetRequiredService<GameService>();
        await Assert.ThrowsExactlyAsync<GameActionException>(() => games.CreateAsync("Unknown"));
        var commanderId = await games.CreateAsync("Commander");
        var regularId = await games.CreateAsync("Regular");
        await games.JoinAsync(commanderId, "One", "10 Island");
        await games.JoinAsync(regularId, "Two", "10 Island");

        var beforeSecondJoin = await games.GetPublicStatsAsync();
        Assert.AreEqual(2, beforeSecondJoin.GamesCreated);
        Assert.AreEqual(0, beforeSecondJoin.GamesPlayed);
        Assert.AreEqual(2, beforeSecondJoin.PlayersJoined);
        Assert.AreEqual(20, beforeSecondJoin.CardsLoaded);
        Assert.AreEqual(0, beforeSecondJoin.RandomCardsDrawn);
        Assert.AreEqual(2, beforeSecondJoin.ActiveTables);

        await games.JoinAsync(commanderId, "Three", "10 Island");
        await games.JoinAsync(commanderId, "Four", "10 Island");
        await games.JoinAsync(commanderId, "Five", "10 Island");
        await Assert.ThrowsExactlyAsync<GameActionException>(() =>
            games.JoinAsync(commanderId, "Six", "10 Island"));
        await games.RecordRandomCardDrawAsync();
        await games.RecordRandomCardDrawAsync();

        var expected = new PublicStats(2, 1, 1, 0, 5, 50, 2, 2);
        Assert.AreEqual(expected, await games.GetPublicStatsAsync());

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Games.ExecuteUpdateAsync(setters =>
                setters.SetProperty(game => game.LastActivityUtc, DateTime.UtcNow.AddDays(-31)));
        }
        Assert.AreEqual(2, await games.CleanupExpiredAsync());
        Assert.AreEqual(expected with { ActiveTables = 0 }, await games.GetPublicStatsAsync());
    }

    [TestMethod]
    public async Task MigrationBackfillsRetainedGames()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var provider = CreateServices(connection);
        var factory = provider.GetRequiredService<IDbContextFactory<PlayMagicDbContext>>();

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync("20260917233555_GameActivity");
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO Games (Id, Format, CreatedUtc, LastActivityUtc) VALUES
                    ('COMMAND1', 'Commander', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                    ('REGULAR1', 'Regular', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP),
                    ('REGULAR2', 'Regular', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
                INSERT INTO Players (Id, GameId, Name, TokenHash, DeckName, Life, CountersJson, JoinedUtc) VALUES
                    ('p1', 'COMMAND1', '', 't1', '', 0, '{{}}', CURRENT_TIMESTAMP),
                    ('p2', 'COMMAND1', '', 't2', '', 0, '{{}}', CURRENT_TIMESTAMP),
                    ('p3', 'REGULAR1', '', 't3', '', 0, '{{}}', CURRENT_TIMESTAMP),
                    ('p4', 'REGULAR2', '', 't4', '', 0, '{{}}', CURRENT_TIMESTAMP),
                    ('p5', 'REGULAR2', '', 't5', '', 0, '{{}}', CURRENT_TIMESTAMP);
                INSERT INTO GameCards (Id, PlayerId, Name, TypeLine, OracleText, Zone, SortOrder, Tapped, CountersJson) VALUES
                    ('c1', 'p1', '', '', '', 'Library', 0, 0, '{{}}'),
                    ('c2', 'p2', '', '', '', 'Library', 0, 0, '{{}}'),
                    ('c3', 'p3', '', '', '', 'Library', 0, 0, '{{}}'),
                    ('c4', 'p4', '', '', '', 'Library', 0, 0, '{{}}');
                """);
        }

        await using (var db = await factory.CreateDbContextAsync())
            await db.Database.MigrateAsync();

        Assert.AreEqual(new PublicStats(3, 2, 1, 1, 5, 4, 0, 3),
            await provider.GetRequiredService<GameService>().GetPublicStatsAsync());
    }

    [TestMethod]
    public async Task ActiveTablesExcludeExpiredRoomsBeforeCleanupRuns()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var provider = CreateServices(connection);
        var factory = provider.GetRequiredService<IDbContextFactory<PlayMagicDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.Games.AddRange(
                new Game { Id = "NEWEMPTY", LastActivityUtc = DateTime.UtcNow.AddHours(-23) },
                new Game { Id = "OLDEMPTY", LastActivityUtc = DateTime.UtcNow.AddHours(-25) },
                new Game { Id = "NEWPLAY1", LastActivityUtc = DateTime.UtcNow.AddDays(-29) },
                new Game { Id = "OLDPLAY1", LastActivityUtc = DateTime.UtcNow.AddDays(-31) });
            db.Players.AddRange(
                new Player { Id = "new-player", GameId = "NEWPLAY1", TokenHash = "new" },
                new Player { Id = "old-player", GameId = "OLDPLAY1", TokenHash = "old" });
            await db.SaveChangesAsync();
        }

        var stats = await provider.GetRequiredService<GameService>().GetPublicStatsAsync();

        Assert.AreEqual(2, stats.ActiveTables);
    }

    private static ServiceProvider CreateServices(SqliteConnection connection)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddDbContextFactory<PlayMagicDbContext>(options => options.UseSqlite(connection));
        services.AddSingleton<CardCatalogService>();
        services.AddSingleton<GameNotifier>();
        services.AddSingleton<GameService>();
        return services.BuildServiceProvider();
    }
}
