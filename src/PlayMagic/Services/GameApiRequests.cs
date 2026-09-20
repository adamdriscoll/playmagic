namespace PlayMagic.Services;

public sealed record CreateGameRequest(string? Format);
public sealed record JoinGameRequest(string? PlayerName, string? DeckText, string? DeckName);
public sealed record ClaimSeatRequest(string? TransferCode);
public sealed record ImportGameRequest(string? Archive);
public sealed record GameApiResponse(string? Value, string? Error);
public sealed record ImportGameApiResponse(GameImportResult? Result, string? Error);
