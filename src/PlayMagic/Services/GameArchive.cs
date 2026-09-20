namespace PlayMagic.Services;

/// <summary>A portable, credential-free snapshot of a game.</summary>
public sealed record GameArchive(
    int Version,
    string Format,
    DateTimeOffset ExportedUtc,
    string ExportedBySeat,
    string? ActiveSeat,
    int TurnNumber,
    List<ArchivedPlayer> Players,
    List<ArchivedGameEvent> Events);

/// <summary>A player in a portable game snapshot. Seat keys are local to the file.</summary>
public sealed record ArchivedPlayer(
    string Seat,
    string Name,
    string DeckName,
    int Life,
    Dictionary<string, int> Counters,
    Dictionary<string, int> CommanderDamage,
    List<ArchivedCard> Cards);

/// <summary>A card in a portable game snapshot. Database IDs and access tokens are never exported.</summary>
public sealed record ArchivedCard(
    string Name,
    string? ImageUrl,
    string? BackImageUrl,
    string TypeLine,
    string OracleText,
    string Zone,
    int SortOrder,
    bool Tapped,
    Dictionary<string, int> Counters);

/// <summary>A recent activity entry in a portable game snapshot.</summary>
public sealed record ArchivedGameEvent(
    string? Seat,
    string PlayerName,
    string Kind,
    string Message,
    DateTimeOffset CreatedUtc);

/// <summary>A single-use seat invitation generated while restoring a game.</summary>
public sealed record RestoredSeatInvite(string PlayerName, string Code);

/// <summary>The new table credentials and replacement invitations returned to the importer.</summary>
public sealed record GameImportResult(string GameId, string Token, List<RestoredSeatInvite> Invites);

