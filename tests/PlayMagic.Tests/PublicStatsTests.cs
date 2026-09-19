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
            db.Games.AddRange(
                new Game { Id = "COMMAND1", Format = "Commander" },
                new Game { Id = "REGULAR1", Format = "Regular" },
                new Game { Id = "REGULAR2", Format = "Regular" });
            db.Players.AddRange(
                new Player { Id = "p1", GameId = "COMMAND1", TokenHash = "t1" },
                new Player { Id = "p2", GameId = "COMMAND1", TokenHash = "t2" },
                new Player { Id = "p3", GameId = "REGULAR1", TokenHash = "t3" },
                new Player { Id = "p4", GameId = "REGULAR2", TokenHash = "t4" },
                new Player { Id = "p5", GameId = "REGULAR2", TokenHash = "t5" });
            db.GameCards.AddRange(
                new GameCard { Id = "c1", PlayerId = "p1" },
                new GameCard { Id = "c2", PlayerId = "p2" },
                new GameCard { Id = "c3", PlayerId = "p3" },
                new GameCard { Id = "c4", PlayerId = "p4" });
            await db.SaveChangesAsync();
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
