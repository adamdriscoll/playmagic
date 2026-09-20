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
    Dictionary<string, int> Counters, Dictionary<string, int> CommanderDamage,
    int LibraryCount, int HandCount, List<CardView> Cards);

/// <summary>A public entry in a table's recent activity.</summary>
public sealed record GameEventView(long Id, string Kind, string Message, DateTimeOffset CreatedUtc);

/// <summary>A game snapshot filtered for one anonymous seat or a public spectator.</summary>
public sealed record GameView(string Id, string Format, string? MyPlayerId, bool IsSpectator,
    string? ActivePlayerId, int TurnNumber, List<PlayerView> Players, List<GameEventView> Events);

/// <summary>Where a card should be placed when it enters a library.</summary>
public enum LibraryPlacement { Top, Bottom }

/// <summary>Public historical totals and the current number of active tables.</summary>
public sealed record PublicStats(long GamesCreated, long GamesPlayed, long CommanderGamesPlayed,
    long RegularGamesPlayed, long PlayersJoined, long CardsLoaded, long RandomCardsDrawn,
    long ActiveTables);

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
    GameNotifier notifier)
{
    private readonly SemaphoreSlim[] _gameLocks = Enumerable.Range(0, 256)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private const string GameAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const string TransferAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
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
        var totals = await db.PublicStatistics.AsNoTracking()
            .Select(stats => new PublicStats(stats.GamesCreated, stats.GamesPlayed,
                stats.CommanderGamesPlayed, stats.RegularGamesPlayed, stats.PlayersJoined,
                stats.CardsLoaded, stats.RandomCardsDrawn, 0))
            .SingleOrDefaultAsync() ?? new PublicStats(0, 0, 0, 0, 0, 0, 0, 0);
        var now = DateTime.UtcNow;
        var emptyCutoff = now - EmptyGameLifetime;
        var inactiveCutoff = now - InactiveGameLifetime;
        var activeTables = await db.Games.AsNoTracking().LongCountAsync(game =>
            game.LastActivityUtc >= inactiveCutoff &&
            (game.LastActivityUtc >= emptyCutoff || db.Players.Any(player => player.GameId == game.Id)));
        return totals with { ActiveTables = activeTables };
    }

    public async Task RecordRandomCardDrawAsync()
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        await IncrementStatisticsAsync(db, randomCardsDrawn: 1);
    }

    public async Task<GameInfo?> GetInfoAsync(string gameId)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        var game = await db.Games.AsNoTracking().FirstOrDefaultAsync(item => item.Id == gameId);
        if (game is null) return null;
        return new GameInfo(game.Id, game.Format, await db.Players.CountAsync(player => player.GameId == gameId));
    }

    public async Task<string> JoinAsync(string gameId, string playerName, string? deckText, string? deckName = null)
    {
        if (gameId.Length != 8 || gameId.Any(character => !GameAlphabet.Contains(character)))
            throw new GameActionException("This game code does not exist.");
        playerName = playerName.Trim();
        if (playerName.Length is < 1 or > 24) throw new GameActionException("Choose a name of 1 to 24 characters.");
        deckName = deckName?.Trim();
        if (deckName?.Length > 80) throw new GameActionException("Deck names must be 80 characters or fewer.");
        await using (var preflightDb = await contextFactory.CreateDbContextAsync())
        {
            if (!await preflightDb.Games.AnyAsync(game => game.Id == gameId))
                throw new GameActionException("This game code does not exist.");
            if (await preflightDb.Players.CountAsync(player => player.GameId == gameId) >= 4)
                throw new GameActionException("This game already has four players.");
        }
        var imported = DeckImportService.ParseText(deckText);
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
            if (game.ActivePlayerId is null) game.ActivePlayerId = player.Id;
            AddEvent(db, gameId, player, "Join", $"{player.Name} joined the table.");
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
        try { return await GetViewUnderLockAsync(gameId, token, spectator: false); }
        finally { gate.Release(); }
    }

    public async Task<GameView?> GetSpectatorViewAsync(string gameId)
    {
        var gate = GateFor(gameId);
        await gate.WaitAsync();
        try { return await GetViewUnderLockAsync(gameId, null, spectator: true); }
        finally { gate.Release(); }
    }

    private async Task<GameView?> GetViewUnderLockAsync(string gameId, string? token, bool spectator)
    {
        await using var db = await contextFactory.CreateDbContextAsync();
        var game = await db.Games.AsNoTracking().FirstOrDefaultAsync(item => item.Id == gameId);
        if (game is null) return null;
        var players = (await db.Players.AsNoTracking().Where(player => player.GameId == gameId)
            .ToListAsync()).OrderBy(player => player.JoinedUtc).ToList();
        var mine = spectator ? null : players.FirstOrDefault(player => player.TokenHash == HashToken(token!));
        if (!spectator && mine is null) return null;
        var now = DateTime.UtcNow;
        if (!spectator && game.LastActivityUtc < now.AddMinutes(-15))
            await db.Games.Where(item => item.Id == gameId && item.LastActivityUtc < now.AddMinutes(-15))
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LastActivityUtc, now));
        var playerIds = players.Select(player => player.Id).ToList();
        var cards = await db.GameCards.AsNoTracking().Where(card => playerIds.Contains(card.PlayerId)).ToListAsync();
        var events = await db.GameEvents.AsNoTracking().Where(item => item.GameId == gameId)
            .OrderByDescending(item => item.Id).Take(20)
            .Select(item => new GameEventView(item.Id, item.Kind, item.Message, item.CreatedUtc))
            .ToListAsync();
        var views = players.Select(player =>
        {
            var playerCards = cards.Where(card => card.PlayerId == player.Id).ToList();
            return new PlayerView(player.Id, player.Name, player.DeckName, player.Life,
                DeserializeCounters(player.CountersJson), DeserializeCounters(player.CommanderDamageJson),
                playerCards.Count(card => card.Zone == CardZones.Library),
                playerCards.Count(card => card.Zone == CardZones.Hand),
                playerCards.Where(card => card.Zone is not (CardZones.Library or CardZones.Hand)
                    || player.Id == mine?.Id && card.Zone == CardZones.Hand)
                    .OrderBy(card => card.SortOrder)
                    .Select(card => new CardView(card.Id, card.Name, card.ImageUrl, card.BackImageUrl,
                        card.TypeLine, card.OracleText, card.Zone, card.Tapped, DeserializeCounters(card.CountersJson)))
                    .ToList());
        }).ToList();
        return new GameView(gameId, game.Format, mine?.Id, spectator, game.ActivePlayerId,
            game.TurnNumber, views, events);
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
        AddEvent(db, gameId, player, "Shuffle", $"{player.Name} shuffled their library.");
    });

    public Task MulliganAsync(string gameId, string token) => MutateAsync(gameId, token, async (db, player) =>
    {
        var cards = await db.GameCards.Where(card => card.PlayerId == player.Id).ToListAsync();
        foreach (var card in cards)
        {
            card.Zone = CardZones.Library;
            card.Tapped = false;
            card.CountersJson = "{}";
        }
        Shuffle(cards);
        for (var index = 0; index < cards.Count; index++)
        {
            cards[index].SortOrder = index;
            if (index < Math.Min(7, cards.Count)) cards[index].Zone = CardZones.Hand;
        }
        AddEvent(db, gameId, player, "Mulligan", $"{player.Name} took a mulligan and drew seven cards.");
    });

    public Task MillAsync(string gameId, string token, int count = 1) => MutateAsync(gameId, token, async (db, player) =>
    {
        if (count is < 1 or > 10) throw new GameActionException("Mill between one and ten cards.");
        var cards = await db.GameCards.Where(card => card.PlayerId == player.Id && card.Zone == CardZones.Library)
            .OrderBy(card => card.SortOrder).Take(count).ToListAsync();
        foreach (var card in cards) card.Zone = CardZones.Graveyard;
        var names = cards.Count == 0 ? "no cards" : string.Join(", ", cards.Select(card => card.Name));
        AddEvent(db, gameId, player, "Mill", $"{player.Name} milled {names}.");
    });

    public async Task<CardView?> ScryAsync(string gameId, string token)
    {
        var gate = GateFor(gameId);
        await gate.WaitAsync();
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync();
            var hash = HashToken(token);
            var player = await db.Players.AsNoTracking().FirstOrDefaultAsync(item => item.GameId == gameId && item.TokenHash == hash)
                ?? throw new GameActionException("Your game seat could not be found. Rejoin from this browser.");
            var card = await db.GameCards.AsNoTracking()
                .Where(item => item.PlayerId == player.Id && item.Zone == CardZones.Library)
                .OrderBy(item => item.SortOrder).FirstOrDefaultAsync();
            return card is null ? null : ToCardView(card);
        }
        finally { gate.Release(); }
    }

    public Task RevealTopAsync(string gameId, string token) => MutateAsync(gameId, token, async (db, player) =>
    {
        var card = await db.GameCards.Where(item => item.PlayerId == player.Id && item.Zone == CardZones.Library)
            .OrderBy(item => item.SortOrder).FirstOrDefaultAsync()
            ?? throw new GameActionException("Your library is empty.");
        AddEvent(db, gameId, player, "Reveal", $"{player.Name} revealed {card.Name} from the top of their library.");
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

    public Task MoveCardAsync(string gameId, string token, string cardId, string zone,
        LibraryPlacement placement = LibraryPlacement.Top) => MutateAsync(gameId, token, async (db, player) =>
    {
        if (!CardZones.IsValid(zone)) throw new GameActionException("Unknown card zone.");
        var card = await db.GameCards.FirstOrDefaultAsync(item => item.Id == cardId && item.PlayerId == player.Id)
            ?? throw new GameActionException("You can move only your own cards.");
        card.Zone = zone;
        if (zone == CardZones.Library)
        {
            var orders = db.GameCards.Where(item => item.PlayerId == player.Id && item.Zone == zone && item.Id != cardId);
            card.SortOrder = placement == LibraryPlacement.Top
                ? (await orders.MinAsync(item => (int?)item.SortOrder) ?? 0) - 1
                : (await orders.MaxAsync(item => (int?)item.SortOrder) ?? 0) + 1;
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

    public Task AdjustCommanderDamageAsync(string gameId, string token, string sourcePlayerId, int delta) =>
        MutateAsync(gameId, token, async (db, player) =>
        {
            if (delta is not (-1 or 1)) throw new GameActionException("Change commander damage by one at a time.");
            var game = await db.Games.AsNoTracking().FirstAsync(item => item.Id == gameId);
            if (game.Format != "Commander") throw new GameActionException("Commander damage is available only in Commander games.");
            var source = await db.Players.AsNoTracking().FirstOrDefaultAsync(item => item.GameId == gameId && item.Id == sourcePlayerId)
                ?? throw new GameActionException("That commander could not be found.");
            var damage = DeserializeCounters(player.CommanderDamageJson);
            var value = Math.Clamp(damage.GetValueOrDefault(sourcePlayerId) + delta, 0, 999);
            if (value == 0) damage.Remove(sourcePlayerId); else damage[sourcePlayerId] = value;
            player.CommanderDamageJson = JsonSerializer.Serialize(damage);
            if (value == 21) AddEvent(db, gameId, player, "Commander", $"{player.Name} reached 21 commander damage from {source.Name}.");
        });

    public async Task<int> RollDieAsync(string gameId, string token, int sides)
    {
        if (sides is < 2 or > 100) throw new GameActionException("Choose a die with 2 to 100 sides.");
        var result = RandomNumberGenerator.GetInt32(1, sides + 1);
        await MutateAsync(gameId, token, (db, player) =>
        {
            AddEvent(db, gameId, player, "Roll", $"{player.Name} rolled {result} on a d{sides}.");
            return Task.CompletedTask;
        });
        return result;
    }

    public async Task<string> FlipCoinAsync(string gameId, string token)
    {
        var result = RandomNumberGenerator.GetInt32(2) == 0 ? "Heads" : "Tails";
        await MutateAsync(gameId, token, (db, player) =>
        {
            AddEvent(db, gameId, player, "Coin", $"{player.Name} flipped {result.ToLowerInvariant()}.");
            return Task.CompletedTask;
        });
        return result;
    }

    public Task SetActivePlayerAsync(string gameId, string token, string activePlayerId) =>
        MutateAsync(gameId, token, async (db, player) =>
        {
            var game = await db.Games.FirstAsync(item => item.Id == gameId);
            var active = await db.Players.AsNoTracking().FirstOrDefaultAsync(item => item.GameId == gameId && item.Id == activePlayerId)
                ?? throw new GameActionException("That player could not be found.");
            if (game.ActivePlayerId != active.Id) game.TurnNumber++;
            game.ActivePlayerId = active.Id;
            AddEvent(db, gameId, player, "Turn", $"{player.Name} passed the turn to {active.Name} (turn {game.TurnNumber}).");
        });

    public Task AdvanceTurnAsync(string gameId, string token) => MutateAsync(gameId, token, async (db, player) =>
    {
        var game = await db.Games.FirstAsync(item => item.Id == gameId);
        var players = (await db.Players.AsNoTracking().Where(item => item.GameId == gameId).ToListAsync())
            .OrderBy(item => item.JoinedUtc).ToList();
        if (players.Count == 0) return;
        var current = players.FindIndex(item => item.Id == game.ActivePlayerId);
        var next = players[(current + 1 + players.Count) % players.Count];
        game.ActivePlayerId = next.Id;
        game.TurnNumber++;
        AddEvent(db, gameId, player, "Turn", $"Turn {game.TurnNumber} started for {next.Name}.");
    });

    public async Task<string> CreateSeatTransferAsync(string gameId, string token)
    {
        var code = new string(Enumerable.Range(0, 10)
            .Select(_ => TransferAlphabet[RandomNumberGenerator.GetInt32(TransferAlphabet.Length)]).ToArray());
        await MutateAsync(gameId, token, (db, player) =>
        {
            player.TransferTokenHash = HashToken(code);
            player.TransferExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10);
            return Task.CompletedTask;
        });
        return code;
    }

    public async Task<string> ClaimSeatTransferAsync(string gameId, string code)
    {
        code = code.Trim().ToUpperInvariant();
        if (code.Length != 10 || code.Any(character => !TransferAlphabet.Contains(character)))
            throw new GameActionException("That transfer code is invalid or expired.");
        var gate = GateFor(gameId);
        await gate.WaitAsync();
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync();
            var hash = HashToken(code);
            var now = DateTimeOffset.UtcNow;
            var player = await db.Players.FirstOrDefaultAsync(item => item.GameId == gameId && item.TransferTokenHash == hash);
            if (player is null || player.TransferExpiresUtc < now)
                throw new GameActionException("That transfer code is invalid or expired.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            player.TokenHash = HashToken(token);
            player.TransferTokenHash = null;
            player.TransferExpiresUtc = null;
            AddEvent(db, gameId, player, "Seat", $"{player.Name} moved their seat to another device.");
            await db.SaveChangesAsync();
            notifier.Publish(gameId);
            return token;
        }
        finally { gate.Release(); }
    }

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
            var oldEventIds = await db.GameEvents.Where(item => item.GameId == gameId)
                .OrderByDescending(item => item.Id).Skip(100).Select(item => item.Id).ToListAsync();
            if (oldEventIds.Count > 0)
                await db.GameEvents.Where(item => oldEventIds.Contains(item.Id)).ExecuteDeleteAsync();
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
        long regularGamesPlayed = 0, long playersJoined = 0, long cardsLoaded = 0,
        long randomCardsDrawn = 0)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""PublicStatistics""
                (""Id"", ""GamesCreated"", ""GamesPlayed"", ""CommanderGamesPlayed"", ""RegularGamesPlayed"", ""PlayersJoined"", ""CardsLoaded"", ""RandomCardsDrawn"")
            VALUES (1, {gamesCreated}, {gamesPlayed}, {commanderGamesPlayed}, {regularGamesPlayed}, {playersJoined}, {cardsLoaded}, {randomCardsDrawn})
            ON CONFLICT(""Id"") DO UPDATE SET
                ""GamesCreated"" = ""GamesCreated"" + excluded.""GamesCreated"",
                ""GamesPlayed"" = ""GamesPlayed"" + excluded.""GamesPlayed"",
                ""CommanderGamesPlayed"" = ""CommanderGamesPlayed"" + excluded.""CommanderGamesPlayed"",
                ""RegularGamesPlayed"" = ""RegularGamesPlayed"" + excluded.""RegularGamesPlayed"",
                ""PlayersJoined"" = ""PlayersJoined"" + excluded.""PlayersJoined"",
                ""CardsLoaded"" = ""CardsLoaded"" + excluded.""CardsLoaded"",
                ""RandomCardsDrawn"" = ""RandomCardsDrawn"" + excluded.""RandomCardsDrawn""");
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

    private static CardView ToCardView(GameCard card) => new(card.Id, card.Name, card.ImageUrl,
        card.BackImageUrl, card.TypeLine, card.OracleText, card.Zone, card.Tapped,
        DeserializeCounters(card.CountersJson));

    private static void AddEvent(PlayMagicDbContext db, string gameId, Player player, string kind, string message) =>
        db.GameEvents.Add(new GameEvent
        {
            GameId = gameId,
            PlayerId = player.Id,
            PlayerName = player.Name,
            Kind = kind,
            Message = message
        });

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
