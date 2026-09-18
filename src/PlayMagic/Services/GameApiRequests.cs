namespace PlayMagic.Services;

public sealed record CreateGameRequest(string? Format);
public sealed record JoinGameRequest(string? PlayerName, string? DeckText, string? DeckName);
public sealed record GameApiResponse(string? Value, string? Error);
