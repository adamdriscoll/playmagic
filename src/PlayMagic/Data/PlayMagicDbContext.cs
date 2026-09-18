using Microsoft.EntityFrameworkCore;

namespace PlayMagic.Data;

/// <summary>Stores the card catalog and the durable state of anonymous games.</summary>
public sealed class PlayMagicDbContext(DbContextOptions<PlayMagicDbContext> options) : DbContext(options)
{
    public DbSet<CatalogCard> Cards => Set<CatalogCard>();
    public DbSet<CatalogSync> CatalogSyncs => Set<CatalogSync>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<GameCard> GameCards => Set<GameCard>();
    public DbSet<PublicStatistics> PublicStatistics => Set<PublicStatistics>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CatalogCard>().HasIndex(card => card.Name);
        modelBuilder.Entity<CatalogCard>().HasIndex(card => card.OracleId);
        modelBuilder.Entity<Game>().HasIndex(game => game.LastActivityUtc);
        modelBuilder.Entity<Player>().HasIndex(player => new { player.GameId, player.TokenHash }).IsUnique();
        modelBuilder.Entity<Player>().HasOne<Game>().WithMany().HasForeignKey(player => player.GameId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<GameCard>().HasOne<Player>().WithMany().HasForeignKey(card => card.PlayerId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<GameCard>().HasIndex(card => new { card.PlayerId, card.Zone, card.SortOrder });
    }
}

/// <summary>Cumulative public totals that survive game cleanup.</summary>
public sealed class PublicStatistics
{
    public int Id { get; set; } = 1;
    public long GamesCreated { get; set; }
    public long GamesPlayed { get; set; }
    public long CommanderGamesPlayed { get; set; }
    public long RegularGamesPlayed { get; set; }
    public long PlayersJoined { get; set; }
    public long CardsLoaded { get; set; }
}

/// <summary>An English Oracle card and the Scryfall image URLs for its faces.</summary>
public sealed class CatalogCard
{
    public string Id { get; set; } = "";
    public string OracleId { get; set; } = "";
    public string Name { get; set; } = "";
    public string TypeLine { get; set; } = "";
    public string ManaCost { get; set; } = "";
    public string OracleText { get; set; } = "";
    public string? ImageUrl { get; set; }
    public string? BackImageUrl { get; set; }
}

/// <summary>Tracks when the local Oracle catalog was successfully refreshed.</summary>
public sealed class CatalogSync
{
    public int Id { get; set; } = 1;
    public DateTimeOffset LastUpdatedUtc { get; set; }
    public int CardCount { get; set; }
}

/// <summary>A shared game addressed by its short invitation code.</summary>
public sealed class Game
{
    public string Id { get; set; } = "";
    public string Format { get; set; } = "Regular";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>An anonymous seat in one game.</summary>
public sealed class Player
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GameId { get; set; } = "";
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string DeckName { get; set; } = "";
    public int Life { get; set; }
    public string CountersJson { get; set; } = "{}";
    public DateTimeOffset JoinedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One physical card in a player's game deck.</summary>
public sealed class GameCard
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PlayerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ImageUrl { get; set; }
    public string? BackImageUrl { get; set; }
    public string TypeLine { get; set; } = "";
    public string OracleText { get; set; } = "";
    public string Zone { get; set; } = CardZones.Library;
    public int SortOrder { get; set; }
    public bool Tapped { get; set; }
    public string CountersJson { get; set; } = "{}";
}

/// <summary>The simple zones supported by the tabletop.</summary>
public static class CardZones
{
    public const string Library = "Library";
    public const string Hand = "Hand";
    public const string Battlefield = "Battlefield";
    public const string Graveyard = "Graveyard";
    public const string Exile = "Exile";

    public static bool IsValid(string zone) => zone is Library or Hand or Battlefield or Graveyard or Exile;
}
