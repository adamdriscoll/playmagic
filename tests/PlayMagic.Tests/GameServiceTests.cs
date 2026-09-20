using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PlayMagic.Data;
using PlayMagic.Services;
using System.Net;
using System.Text;

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
            tokens.Add(await games.JoinAsync(gameId, $"Player {index + 1}", "10 Island"));

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
            games.JoinAsync(gameId, "Player 5", "10 Island"));
    }

    [TestMethod]
    public async Task TabletopFeaturesAreSharedWhileSpectatorsCannotSeeHands()
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
        var gameId = await games.CreateAsync("Commander");
        var firstToken = await games.JoinAsync(gameId, "One", "10 Island");
        var secondToken = await games.JoinAsync(gameId, "Two", "10 Island");
        var first = (await games.GetViewAsync(gameId, firstToken))!;
        var second = (await games.GetViewAsync(gameId, secondToken))!;

        Assert.AreEqual(first.MyPlayerId, first.ActivePlayerId);
        var spectator = (await games.GetSpectatorViewAsync(gameId))!;
        Assert.IsTrue(spectator.IsSpectator);
        Assert.IsNull(spectator.MyPlayerId);
        Assert.IsFalse(spectator.Players.SelectMany(player => player.Cards).Any(card => card.Zone == CardZones.Hand));

        var roll = await games.RollDieAsync(gameId, firstToken, 20);
        Assert.IsTrue(roll is >= 1 and <= 20);
        await games.FlipCoinAsync(gameId, firstToken);
        spectator = (await games.GetSpectatorViewAsync(gameId))!;
        Assert.IsTrue(spectator.Events.Any(item => item.Kind == "Roll" && item.Message.Contains(roll.ToString())));
        Assert.IsTrue(spectator.Events.Any(item => item.Kind == "Coin"));

        var originalTop = await games.ScryAsync(gameId, firstToken);
        Assert.IsNotNull(originalTop);
        await games.MoveCardAsync(gameId, firstToken, originalTop.Id, CardZones.Library, LibraryPlacement.Bottom);
        Assert.AreNotEqual(originalTop.Id, (await games.ScryAsync(gameId, firstToken))!.Id);
        await games.RevealTopAsync(gameId, firstToken);
        await games.MillAsync(gameId, firstToken);
        first = (await games.GetViewAsync(gameId, firstToken))!;
        Assert.AreEqual(2, first.Players.Single(player => player.Id == first.MyPlayerId).LibraryCount);
        Assert.IsTrue(first.Events.Any(item => item.Kind == "Reveal"));
        Assert.IsTrue(first.Events.Any(item => item.Kind == "Mill"));

        var handCard = first.Players.Single(player => player.Id == first.MyPlayerId).Cards
            .First(card => card.Zone == CardZones.Hand);
        await games.MoveCardAsync(gameId, firstToken, handCard.Id, CardZones.Commander);
        first = (await games.GetViewAsync(gameId, firstToken))!;
        Assert.IsTrue(first.Players.Single(player => player.Id == first.MyPlayerId).Cards
            .Any(card => card.Id == handCard.Id && card.Zone == CardZones.Commander));

        await games.AdjustCommanderDamageAsync(gameId, secondToken, first.MyPlayerId!, 1);
        second = (await games.GetViewAsync(gameId, secondToken))!;
        Assert.AreEqual(1, second.Players.Single(player => player.Id == second.MyPlayerId)
            .CommanderDamage[first.MyPlayerId!]);

        await games.AdvanceTurnAsync(gameId, firstToken);
        first = (await games.GetViewAsync(gameId, firstToken))!;
        Assert.AreEqual(second.MyPlayerId, first.ActivePlayerId);
        Assert.AreEqual(2, first.TurnNumber);

        await games.MulliganAsync(gameId, firstToken);
        first = (await games.GetViewAsync(gameId, firstToken))!;
        Assert.AreEqual(7, first.Players.Single(player => player.Id == first.MyPlayerId).HandCount);
        Assert.IsTrue(first.Events.Any(item => item.Kind == "Mulligan"));
    }

    [TestMethod]
    public async Task SeatTransferIsSingleUseAndRotatesTheSeatToken()
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
        var gameId = await games.CreateAsync("Regular");
        var originalToken = await games.JoinAsync(gameId, "Traveler", "10 Island");
        var transferCode = await games.CreateSeatTransferAsync(gameId, originalToken);

        var replacementToken = await games.ClaimSeatTransferAsync(gameId, transferCode);

        Assert.IsNull(await games.GetViewAsync(gameId, originalToken));
        Assert.IsNotNull(await games.GetViewAsync(gameId, replacementToken));
        await Assert.ThrowsExactlyAsync<GameActionException>(() => games.ClaimSeatTransferAsync(gameId, transferCode));
    }

    [TestMethod]
    public void TextImportOmitsSideboardAndMergesCopies()
    {
        var deck = DeckImportService.ParseText("// MAINBOARD\n2 Island\n1x Island\n// SIDEBOARD\n3 Lightning Bolt\n// COMMANDER\n1 Sol Ring");
        Assert.HasCount(2, deck.Cards);
        Assert.AreEqual(3, deck.Cards.Single(card => card.Name == "Island").Quantity);
        Assert.AreEqual(1, deck.Cards.Single(card => card.Name == "Sol Ring").Quantity);
    }

    [TestMethod]
    public void TextImportAcceptsArenaAndDeckBuilderSections()
    {
        var deck = DeckImportService.ParseText("Deck\n2 Island (DMU) 265\n1 x Sol Ring (CMM)\n" +
            "Sideboard\n3 Negate\nCompanion\n1 Lurrus of the Dream-Den\n" +
            "Commander\n1 Atraxa, Praetors' Voice\n// Maybeboard\n2 Forest\n# Creatures\n1x Birds of Paradise [CN2] 176");

        Assert.HasCount(4, deck.Cards);
        Assert.AreEqual(2, deck.Cards.Single(card => card.Name == "Island").Quantity);
        Assert.AreEqual("265", deck.Cards.Single(card => card.Name == "Island").CollectorNumber);
        Assert.AreEqual("CMM", deck.Cards.Single(card => card.Name == "Sol Ring").SetCode);
        Assert.IsFalse(deck.Cards.Any(card => card.Name is "Negate" or "Lurrus of the Dream-Den" or "Forest"));
    }

    [TestMethod]
    public void TextImportRequiresCards()
    {
        Assert.ThrowsExactly<DeckImportException>(() => DeckImportService.ParseText("https://www.moxfield.com/decks/example"));
        Assert.ThrowsExactly<DeckImportException>(() => DeckImportService.ParseText("Sideboard\n2 Negate"));
    }

    [TestMethod]
    public void TextImportHandlesArchidektCategoryAndLabelAnnotations()
    {
        var deck = DeckImportService.ParseText("1x Sol Ring (CMM) *F* [Artifacts] ^Favorite,#000000^\n" +
            "1x Island (DMU) [Lands]\n1x Negate (M20) [Sideboard] ^Maybe,#000000^");

        Assert.HasCount(2, deck.Cards);
        Assert.AreEqual("CMM", deck.Cards.Single(card => card.Name == "Sol Ring").SetCode);
        Assert.IsFalse(deck.Cards.Any(card => card.Name == "Negate"));
    }

    [TestMethod]
    public async Task CleanupRemovesExpiredGamesAndCascadesToSeatsAndCards()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddHttpClient();
        registrations.AddDbContextFactory<PlayMagicDbContext>(options => options.UseSqlite(connection));
        registrations.AddSingleton<CardCatalogService>();
        registrations.AddSingleton<GameNotifier>();
        registrations.AddSingleton<GameService>();
        using var provider = registrations.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<PlayMagicDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.Games.AddRange(
                new Game { Id = "OLDMPTY1", LastActivityUtc = DateTime.UtcNow.AddHours(-25) },
                new Game { Id = "NEWEMPTY", LastActivityUtc = DateTime.UtcNow.AddHours(-23) },
                new Game { Id = "OLDPLAY1", LastActivityUtc = DateTime.UtcNow.AddDays(-31) },
                new Game { Id = "NEWPLAY1", LastActivityUtc = DateTime.UtcNow.AddDays(-29) });
            db.Players.AddRange(
                new Player { Id = "old-player", GameId = "OLDPLAY1", Name = "Old", TokenHash = "old" },
                new Player { Id = "new-player", GameId = "NEWPLAY1", Name = "New", TokenHash = "new" });
            db.GameCards.Add(new GameCard { Id = "old-card", PlayerId = "old-player", Name = "Island" });
            await db.SaveChangesAsync();
        }

        var removed = await provider.GetRequiredService<GameService>().CleanupExpiredAsync();

        Assert.AreEqual(2, removed);
        await using var verify = await factory.CreateDbContextAsync();
        CollectionAssert.AreEquivalent(new[] { "NEWEMPTY", "NEWPLAY1" },
            await verify.Games.Select(game => game.Id).ToArrayAsync());
        Assert.AreEqual(1, await verify.Players.CountAsync());
        Assert.AreEqual(0, await verify.GameCards.CountAsync());
    }

    [TestMethod]
    public void TextImportParsesFullMoxfieldExport()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MoxfieldExport.txt"));
        var deck = DeckImportService.ParseText(text);

        Assert.HasCount(100, deck.Cards);
        Assert.AreEqual(100, deck.Cards.Sum(card => card.Quantity));
        Assert.IsFalse(deck.Cards.Any(card => card.Name.Contains(" *F*") || card.Name.Contains(" *E*")));
        Assert.IsTrue(deck.Cards.All(card => card.SetCode is not null && card.CollectorNumber is not null));
        Assert.AreEqual("Breya, Etherium Shaper", deck.Cards[0].Name);
        Assert.AreEqual("2XM", deck.Cards[0].SetCode);
        Assert.AreEqual("192", deck.Cards[0].CollectorNumber);
        Assert.AreEqual("2026-1", deck.Cards.Single(card => card.Name == "Command Tower").CollectorNumber);
        Assert.AreEqual("Tony Stark // The Invincible Iron Man",
            deck.Cards.Single(card => card.Name.StartsWith("Tony Stark", StringComparison.Ordinal)).Name);
        Assert.AreEqual("2217", deck.Cards.Single(card => card.Name == "Kings Bay Clock Tower").CollectorNumber);
    }

    [TestMethod]
    public void TextImportParsesMoxfieldListPrintingsAndSkipsSideboard()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MoxfieldListPrintingExport.txt"));
        var deck = DeckImportService.ParseText(text);

        Assert.HasCount(19, deck.Cards);
        Assert.AreEqual(60, deck.Cards.Sum(card => card.Quantity));
        Assert.IsTrue(deck.Cards.All(card => card.SetCode is not null && card.CollectorNumber is not null));
        Assert.AreEqual("A25-46", deck.Cards.Single(card => card.Name == "Brainstorm").CollectorNumber);
        Assert.AreEqual("EMA-45", deck.Cards.Single(card => card.Name == "Deep Analysis").CollectorNumber);
        Assert.AreEqual("IMA-76", deck.Cards.Single(card => card.Name == "Thought Scour").CollectorNumber);
        Assert.IsFalse(deck.Cards.Any(card => card.Name == "Hydroblast"));
    }

    [TestMethod]
    public async Task AlternatePrintedNamesResolveToOracleCards()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var handler = new PrintingHandler();
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddHttpClient("Scryfall", client => client.BaseAddress = new Uri("https://api.scryfall.com/"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        registrations.AddDbContextFactory<PlayMagicDbContext>(options => options.UseSqlite(connection));
        registrations.AddSingleton<CardCatalogService>();
        registrations.AddSingleton<GameNotifier>();
        registrations.AddSingleton<GameService>();
        using var provider = registrations.BuildServiceProvider();

        await using (var db = await provider.GetRequiredService<IDbContextFactory<PlayMagicDbContext>>().CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            foreach (var name in new[] { "Midnight Clock", "Academy Ruins", "Fellwar Stone", "Thran Dynamo" })
                db.Cards.Add(new CatalogCard { Id = name, OracleId = name, Name = name });
            db.CatalogSyncs.Add(new CatalogSync { Id = 1, CardCount = 4, LastUpdatedUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var games = provider.GetRequiredService<GameService>();
        var gameId = await games.CreateAsync("Commander");
        var token = await games.JoinAsync(gameId, "Player",
            "1 Kings Bay Clock Tower (SLD) 2217 *F*\n" +
            "1 Kitezh, Sunken City (SLD) 1506 *F*\n" +
            "1 Shu Jing Meteorite (SLD) 7062 *F*\n" +
            "1 The Hexcore (SLD) 483 *F*");
        var view = await games.GetViewAsync(gameId, token);

        Assert.IsNotNull(view);
        Assert.HasCount(4, view.Players[0].Cards);
        Assert.IsTrue(view.Players[0].Cards.All(card =>
            card.Name is "Midnight Clock" or "Academy Ruins" or "Fellwar Stone" or "Thran Dynamo"));
        Assert.AreEqual(4, handler.RequestCount);
    }

    private sealed class PrintingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var card = request.RequestUri?.AbsolutePath switch
            {
                "/cards/sld/2217" => ("Midnight Clock", "Kings Bay Clock Tower"),
                "/cards/sld/1506" => ("Academy Ruins", "Kitezh, Sunken City"),
                "/cards/sld/7062" => ("Fellwar Stone", "Shu Jing Meteorite"),
                "/cards/sld/483" => ("Thran Dynamo", "The Hexcore"),
                _ => default
            };
            if (card.Item1 is null)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                name = card.Item1,
                flavor_name = card.Item2,
                oracle_id = card.Item1
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
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
