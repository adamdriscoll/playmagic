using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlayMagic.Data;

namespace PlayMagic.Services;

/// <summary>Public information needed before a visitor joins a game.</summary>
public sealed record GameInfo(string Id, string Format, int PlayerCount);

/// <summary>One card visible in a player's private or public area.</summary>
public sealed record CardView(string Id, string Name, string? ImageUrl, string? BackImageUrl,
    string TypeLine, string OracleText, string Zone, bool Tapped, Dictionary<string, int> Counters);

/// <summary>A player's public state, with hand cards included only for the requesting player.</summary>
public sealed record PlayerView(string Id, string Name, string DeckName, int Life,
    Dictionary<string, int> Counters, int LibraryCount, int HandCount, List<CardView> Cards);

/// <summary>A game snapshot filtered for one anonymous seat.</summary>
public sealed record GameView(string Id, string Format, string MyPlayerId, List<PlayerView> Players);

/// <summary>Cumulative public totals, independent of retained game records.</summary>
public sealed record PublicStats(long GamesCreated, long GamesPlayed, long CommanderGamesPlayed,
    long RegularGamesPlayed, long PlayersJoined, long CardsLoaded);

/// <summary>A user-facing game action error.</summary>
public sealed class GameActionException(string message) : Exception(message);

/// <summary>Notifies active Blazor circuits after a committed game change.</summary>
public sealed class GameNotifier
{
    private readonly Dictionary<string, Dictionary<Guid, Action>> _subscribers = new();
    private readonly object _gate = new();

    public IDisposable Subscribe(string gameId, Action callback)
    {
        var key = Guid.NewGuid();
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(gameId, out var listeners))
                _subscribers[gameId] = listeners = new();
            listeners[key] = callback;
        }
        return new Subscription(() =>
        {
            lock (_gate)
            {
                if (!_subscribers.TryGetValue(gameId, out var listeners)) return;
                listeners.Remove(key);
                if (listeners.Count == 0) _subscribers.Remove(gameId);
            }
        });
    }

    public void Publish(string gameId)
    {
        Action[] callbacks;
        lock (_gate)
            callbacks = _subscribers.TryGetValue(gameId, out var listeners)
                ? listeners.Values.ToArray() : [];
        foreach (var callback in callbacks) callback();
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}

/// <summary>Creates games and applies validated, persistent tabletop actions.</summary>
public sealed class GameService(
    IDbContextFactory<PlayMagicDbContext> contextFactory,
    CardCatalogService catalog,
    DeckImportService deckImport,
    GameNotifier notifier)
{
    private readonly SemaphoreSlim[] _gameLocks = Enumerable.Range(0, 256)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private const string GameAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private static readonly TimeSpan EmptyGameLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan InactiveGameLifetime = TimeSpan.FromDays(30);

    private SemaphoreSlim GateFor(string gameId) =>
        _gameLocks[(uint)StringComparer.Ordinal.GetHashCode(gameId) % (uint)_gameLocks.Length];

    public async Task<string> CreateAsync(string? format)
    {
        if (format is not ("Commander" or "Regular")) throw new GameActionException("Choose Commander or Regular.");
        await using var db = await contextFactory.CreateDbContextAsync();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var id = new string(Enumerable.Range(0, 8)
                .Select(_ => GameAlphabet[RandomNumberGenerator.GetInt32(GameAlphabet.Length)]).ToArray());
            if (await db.Games.AnyAsync(game => game.Id == id)) continue;
            await using var transaction = await db.Database.BeginTransactionAsync();
            db.Games.Add(new Game { Id = id, Format = format });
            await db.SaveChangesAsync();
            await IncrementStatisticsAsync(db, gamesCreated: 1);
            await transaction.CommitAsync();
            return id;
        }
        throw new GameActionException("Could not make a game code. Please try again.");
    }

    public async Task<PublicStats> GetPublicStatsAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        return await db.PublicStatistics.AsNoTracking()
            .Select(stats => new PublicStats(stats.GamesCreated, stats.GamesPlayed,
                stats.CommanderGamesPlayed, stats.RegularGamesPlayed, stats.PlayersJoined, stats.CardsLoaded))
            .SingleOrDefaultAsync() ?? new PublicStats(0, 0, 0, 0, 0, 0);
    }

    public async Task<GameInfo?> GetInfoAsync(string gameId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        var game = await db.Games.AsNoTracking().FirstOrDefaultAsync(item => item.Id == gameId);
        if (game is null) return null;
        return new GameInfo(game.Id, game.Format, await db.Players.CountAsync(player => player.GameId == gameId));
    }

    public async Task<string> JoinAsync(string gameId, string playerName, string? deckUrl, string? deckText, string? deckName = null)
    {
        if (gameId.Length != 8 || gameId.Any(character => !GameAlphabet.Contains(character)))
            throw new GameActionException("This game code does not exist.");
        playerName = playerName.Trim();
        if (playerName.Length is < 1 or > 24) throw new GameActionException("Choose a name of 1 to 24 characters.");
        if (deckUrl?.Length > 2048) throw new GameActionException("Deck URLs must be 2048 characters or fewer.");
        deckName = deckName?.Trim();
        if (deckName?.Length > 80) throw new GameActionException("Deck names must be 80 characters or fewer.");
        await using (var preflightDb = await contextFactory.CreateDbContextAsync())
        {
            if (!await preflightDb.Games.AnyAsync(game => game.Id == gameId))
                throw new GameActionException("This game code does not exist.");
            if (await preflightDb.Players.CountAsync(player => player.GameId == gameId) >= 4)
                throw new GameActionException("This game already has four players.");
        }
        var imported = await deckImport.ImportAsync(deckUrl, deckText);
        if (imported.Cards.Sum(card => card.Quantity) > 250) throw new GameActionException("A deck can contain at most 250 cards.");
        var status = await catalog.GetStatusAsync();
        if (status.Count == 0) throw new GameActionException("The card catalog is still downloading. Please try again shortly.");

        var gate = GateFor(gameId);
        await gate.WaitAsync();
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync();
            var game = await db.Games.FirstOrDefaultAsync(item => item.Id == gameId)
                ?? throw new GameActionException("This game code does not exist.");
            var existingPlayers = await db.Players.CountAsync(player => player.GameId == gameId);
            if (existingPlayers >= 4)
                throw new GameActionException("This game already has four players.");

            var catalogCards = await db.Cards.AsNoTracking().ToListAsync();
            var byName = catalogCards.GroupBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var byOracleId = catalogCards.GroupBy(card => card.OracleId)
                .ToDictionary(group => group.Key, group => group.First());
            var resolved = new Dictionary<string, CatalogCard>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            foreach (var entry in imported.Cards)
            {
                if (!byName.TryGetValue(entry.Name, out var source))
                    source = catalogCards.FirstOrDefault(card =>
                        card.Name.StartsWith(entry.Name + " // ", StringComparison.OrdinalIgnoreCase));
                if (source is null && entry.SetCode is not null && entry.CollectorNumber is not null)
                {
                    try
                    {
                        var oracleId = await catalog.GetOracleIdForPrintingAsync(
                            entry.SetCode, entry.CollectorNumber, entry.Name);
                        if (oracleId is not null) byOracleId.TryGetValue(oracleId, out source);
                    }
                    catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException)
                    {
                        throw new GameActionException("Could not verify alternate card names with Scryfall. Please try again.");
                    }
                }
                if (source is null)
                {
                    if (missing.Count < 5) missing.Add(entry.Name);
                }
                else resolved[entry.Name] = source;
            }
            if (missing.Count > 0) throw new GameActionException($"Cards not found in the Oracle catalog: {string.Join(", ", missing)}.");

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var player = new Player
            {
                GameId = gameId,
                Name = playerName,
                TokenHash = HashToken(token),
                DeckName = string.IsNullOrWhiteSpace(deckName) ? imported.Name : deckName,
                DeckUrl = imported.SourceUrl,
                Life = game.Format == "Commander" ? 40 : 20
            };
            db.Players.Add(player);
            var cards = new List<GameCard>();
            foreach (var entry in imported.Cards)
            {
                var source = resolved[entry.Name];
                for (var index = 0; index < entry.Quantity; index++)
                    cards.Add(new GameCard
                    {
                        PlayerId = player.Id, Name = source.Name, ImageUrl = source.ImageUrl,
                        BackImageUrl = source.BackImageUrl, TypeLine = source.TypeLine,
                        OracleText = source.OracleText
                    });
            }
            Shuffle(cards);
            for (var index = 0; index < cards.Count; index++)
            {
                cards[index].SortOrder = index;
                if (index < Math.Min(7, cards.Count)) cards[index].Zone = CardZones.Hand;
            }
            db.GameCards.AddRange(cards);
            game.LastActivityUtc = DateTime.UtcNow;
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.SaveChangesAsync();
            var started = existingPlayers == 1;
            await IncrementStatisticsAsync(db, gamesPlayed: started ? 1 : 0,
                commanderGamesPlayed: started && game.Format == "Commander" ? 1 : 0,
                regularGamesPlayed: started && game.Format == "Regular" ? 1 : 0,
                playersJoined: 1, cardsLoaded: cards.Count);
            await transaction.CommitAsync();
            notifier.Publish(gameId);
            return token;
        }
        finally { gate.Release(); }
    }

    public async Task<GameView?> GetViewAsync(string gameId, string token)
    {
        var gate = GateFor(gameId);
        await gate.WaitAsync();
        try { return await GetViewUnderLockAsync(gameId, token); }
        finally { gate.Release(); }
    }

    private async Task<GameView?> GetViewUnderLockAsync(string gameId, string token)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        var game = await db.Games.AsNoTracking().FirstOrDefaultAsync(item => item.Id == gameId);
        if (game is null) return null;
        var players = (await db.Players.AsNoTracking().Where(player => player.GameId == gameId)
            .ToListAsync()).OrderBy(player => player.JoinedUtc).ToList();
        var mine = players.FirstOrDefault(player => player.TokenHash == HashToken(token));
        if (mine is null) return null;
        var now = DateTime.UtcNow;
        if (game.LastActivityUtc < now.AddMinutes(-15))
            await db.Games.Where(item => item.Id == gameId && item.LastActivityUtc < now.AddMinutes(-15))
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LastActivityUtc, now));
        var playerIds = players.Select(player => player.Id).ToList();
        var cards = await db.GameCards.AsNoTracking().Where(card => playerIds.Contains(card.PlayerId)).ToListAsync();
        var views = players.Select(player =>
        {
            var playerCards = cards.Where(card => card.PlayerId == player.Id).ToList();
            return new PlayerView(player.Id, player.Name, player.DeckName, player.Life,
                DeserializeCounters(player.CountersJson),
                playerCards.Count(card => card.Zone == CardZones.Library),
                playerCards.Count(card => card.Zone == CardZones.Hand),
                playerCards.Where(card => card.Zone is not (CardZones.Library or CardZones.Hand)
                    || player.Id == mine.Id && card.Zone == CardZones.Hand)
                    .OrderBy(card => card.SortOrder)
                    .Select(card => new CardView(card.Id, card.Name, card.ImageUrl, card.BackImageUrl,
                        card.TypeLine, card.OracleText, card.Zone, card.Tapped, DeserializeCounters(card.CountersJson)))
                    .ToList());
        }).ToList();
        return new GameView(gameId, game.Format, mine.Id, views);
    }

    public Task DrawAsync(string gameId, string token, int count = 1) => MutateAsync(gameId, token, async (db, player) =>
    {
        if (count is < 1 or > 7) throw new GameActionException("Draw between one and seven cards.");
        var cards = await db.GameCards.Where(card => card.PlayerId == player.Id && card.Zone == CardZones.Library)
            .OrderBy(card => card.SortOrder).Take(count).ToListAsync();
        foreach (var card in cards) card.Zone = CardZones.Hand;
    });

    public Task ShuffleAsync(string gameId, string token) => MutateAsync(gameId, token, async (db, player) =>
    {
        var cards = await db.GameCards.Where(card => card.PlayerId == player.Id && card.Zone == CardZones.Library).ToListAsync();
        Shuffle(cards);
        for (var index = 0; index < cards.Count; index++) cards[index].SortOrder = index;
    });

    public Task AdjustLifeAsync(string gameId, string token, int delta) => MutateAsync(gameId, token, (_, player) =>
    {
        if (delta is < -20 or > 20) throw new GameActionException("Life changes must be between -20 and 20.");
        player.Life = Math.Clamp(player.Life + delta, -999, 999);
        return Task.CompletedTask;
    });

    public Task AdjustPlayerCounterAsync(string gameId, string token, string name, int delta) => MutateAsync(gameId, token, (_, player) =>
    {
        player.CountersJson = AdjustCounter(player.CountersJson, name, delta);
        return Task.CompletedTask;
    });

    public Task MoveCardAsync(string gameId, string token, string cardId, string zone) => MutateAsync(gameId, token, async (db, player) =>
    {
        if (!CardZones.IsValid(zone)) throw new GameActionException("Unknown card zone.");
        var card = await db.GameCards.FirstOrDefaultAsync(item => item.Id == cardId && item.PlayerId == player.Id)
            ?? throw new GameActionException("You can move only your own cards.");
        card.Zone = zone;
        if (zone == CardZones.Library)
        {
            var first = await db.GameCards.Where(item => item.PlayerId == player.Id && item.Zone == zone && item.Id != cardId)
                .MinAsync(item => (int?)item.SortOrder);
            card.SortOrder = (first ?? 0) - 1;
        }
        if (zone != CardZones.Battlefield) card.Tapped = false;
    });

    public Task ToggleTappedAsync(string gameId, string token, string cardId) => MutateAsync(gameId, token, async (db, player) =>
    {
        var card = await db.GameCards.FirstOrDefaultAsync(item => item.Id == cardId && item.PlayerId == player.Id && item.Zone == CardZones.Battlefield)
            ?? throw new GameActionException("You can tap only your own battlefield cards.");
        card.Tapped = !card.Tapped;
    });

    public Task AdjustCardCounterAsync(string gameId, string token, string cardId, string name, int delta) => MutateAsync(gameId, token, async (db, player) =>
    {
        var card = await db.GameCards.FirstOrDefaultAsync(item => item.Id == cardId && item.PlayerId == player.Id)
            ?? throw new GameActionException("You can change counters only on your own cards.");
        card.CountersJson = AdjustCounter(card.CountersJson, name, delta);
    });

    private async Task MutateAsync(string gameId, string token, Func<PlayMagicDbContext, Player, Task> change)
    {
        var gate = GateFor(gameId);
        await gate.WaitAsync();
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync();
            var hash = HashToken(token);
            var player = await db.Players.FirstOrDefaultAsync(item => item.GameId == gameId && item.TokenHash == hash)
                ?? throw new GameActionException("Your game seat could not be found. Rejoin from this browser.");
            await change(db, player);
            var game = await db.Games.FirstAsync(item => item.Id == gameId);
            game.LastActivityUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            notifier.Publish(gameId);
        }
        finally { gate.Release(); }
    }

    public async Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var emptyCutoff = now - EmptyGameLifetime;
        var inactiveCutoff = now - InactiveGameLifetime;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var expiredIds = await db.Games.AsNoTracking()
            .Where(game => game.LastActivityUtc < inactiveCutoff ||
                game.LastActivityUtc < emptyCutoff && !db.Players.Any(player => player.GameId == game.Id))
            .Select(game => game.Id).ToListAsync(cancellationToken);
        var removed = 0;
        foreach (var id in expiredIds)
        {
            var gate = GateFor(id);
            await gate.WaitAsync(cancellationToken);
            try
            {
                removed += await db.Games.Where(game => game.Id == id &&
                    (game.LastActivityUtc < inactiveCutoff ||
                     game.LastActivityUtc < emptyCutoff && !db.Players.Any(player => player.GameId == game.Id)))
                    .ExecuteDeleteAsync(cancellationToken);
            }
            finally { gate.Release(); }
        }
        return removed;
    }

    private static async Task IncrementStatisticsAsync(PlayMagicDbContext db,
        long gamesCreated = 0, long gamesPlayed = 0, long commanderGamesPlayed = 0,
        long regularGamesPlayed = 0, long playersJoined = 0, long cardsLoaded = 0)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""PublicStatistics""
                (""Id"", ""GamesCreated"", ""GamesPlayed"", ""CommanderGamesPlayed"", ""RegularGamesPlayed"", ""PlayersJoined"", ""CardsLoaded"")
            VALUES (1, {gamesCreated}, {gamesPlayed}, {commanderGamesPlayed}, {regularGamesPlayed}, {playersJoined}, {cardsLoaded})
            ON CONFLICT(""Id"") DO UPDATE SET
                ""GamesCreated"" = ""GamesCreated"" + excluded.""GamesCreated"",
                ""GamesPlayed"" = ""GamesPlayed"" + excluded.""GamesPlayed"",
                ""CommanderGamesPlayed"" = ""CommanderGamesPlayed"" + excluded.""CommanderGamesPlayed"",
                ""RegularGamesPlayed"" = ""RegularGamesPlayed"" + excluded.""RegularGamesPlayed"",
                ""PlayersJoined"" = ""PlayersJoined"" + excluded.""PlayersJoined"",
                ""CardsLoaded"" = ""CardsLoaded"" + excluded.""CardsLoaded""");
    }

    private static string AdjustCounter(string json, string name, int delta)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 24) throw new GameActionException("Counter names must be 1 to 24 characters.");
        if (delta is not (-1 or 1)) throw new GameActionException("Change a counter by one at a time.");
        var counters = DeserializeCounters(json);
        var value = Math.Clamp(counters.GetValueOrDefault(name) + delta, 0, 999);
        if (value == 0) counters.Remove(name);
        else counters[name] = value;
        return JsonSerializer.Serialize(counters);
    }

    private static Dictionary<string, int> DeserializeCounters(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, int>>(json) ?? new();

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static void Shuffle<T>(IList<T> cards)
    {
        for (var index = cards.Count - 1; index > 0; index--)
        {
            var other = RandomNumberGenerator.GetInt32(index + 1);
            (cards[index], cards[other]) = (cards[other], cards[index]);
        }
    }
}
