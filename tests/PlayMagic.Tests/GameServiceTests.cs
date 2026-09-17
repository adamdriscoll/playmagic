using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlayMagic.Data;
using PlayMagic.Services;

namespace PlayMagic.Tests;

[TestClass]
public sealed class GameServiceTests
{
    [TestMethod]
    public async Task FourSeatsKeepHandsPrivateAndRejectFifthPlayer()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddHttpClient();
        registrations.AddDbContextFactory<PlayMagicDbContext>(options => options.UseSqlite(connection));
        registrations.AddSingleton<CardCatalogService>();
        registrations.AddSingleton<DeckImportService>();
        registrations.AddSingleton<GameNotifier>();
        registrations.AddSingleton<GameService>();
        using var provider = registrations.BuildServiceProvider();

        await using (var db = await provider.GetRequiredService<IDbContextFactory<PlayMagicDbContext>>().CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.Cards.Add(new CatalogCard { Id = "island", OracleId = "island", Name = "Island", TypeLine = "Basic Land — Island" });
            db.CatalogSyncs.Add(new CatalogSync { Id = 1, CardCount = 1, LastUpdatedUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var games = provider.GetRequiredService<GameService>();
        var gameId = await games.CreateAsync("Commander");
        var tokens = new List<string>();
        for (var index = 0; index < 4; index++)
            tokens.Add(await games.JoinAsync(gameId, $"Player {index + 1}", null, "10 Island"));

        var firstView = await games.GetViewAsync(gameId, tokens[0]);
        var secondView = await games.GetViewAsync(gameId, tokens[1]);
        Assert.IsNotNull(firstView);
        Assert.IsNotNull(secondView);
        Assert.HasCount(4, firstView.Players);
        Assert.AreEqual(40, firstView.Players[0].Life);
        Assert.AreEqual(7, firstView.Players[0].Cards.Count(card => card.Zone == CardZones.Hand));
        Assert.AreEqual(7, firstView.Players[1].HandCount);
        Assert.AreEqual(0, firstView.Players[1].Cards.Count(card => card.Zone == CardZones.Hand));
        Assert.AreEqual(7, secondView.Players[1].Cards.Count(card => card.Zone == CardZones.Hand));

        var secondPlayerCard = secondView.Players[1].Cards.First(card => card.Zone == CardZones.Hand);
        await Assert.ThrowsExactlyAsync<GameActionException>(() =>
            games.MoveCardAsync(gameId, tokens[0], secondPlayerCard.Id, CardZones.Battlefield));
        await Assert.ThrowsExactlyAsync<GameActionException>(() =>
            games.JoinAsync(gameId, "Player 5", null, "10 Island"));
    }

    [TestMethod]
    public void TextImportOmitsSideboardAndMergesCopies()
    {
        var deck = DeckImportService.ParseText("// MAINBOARD\n2 Island\n1x Island\n// SIDEBOARD\n3 Lightning Bolt\n// COMMANDER\n1 Sol Ring");
        Assert.HasCount(2, deck.Cards);
        Assert.AreEqual(3, deck.Cards.Single(card => card.Name == "Island").Quantity);
        Assert.AreEqual(1, deck.Cards.Single(card => card.Name == "Sol Ring").Quantity);
    }
}
