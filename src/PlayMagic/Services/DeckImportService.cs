using System.Text.Json;
using System.Text.RegularExpressions;

namespace PlayMagic.Services;

/// <summary>A card and the number of copies in an imported deck.</summary>
public sealed record DeckEntry(string Name, int Quantity);

/// <summary>A deck ready to turn into physical game cards.</summary>
public sealed record ImportedDeck(string Name, string? SourceUrl, IReadOnlyList<DeckEntry> Cards);

/// <summary>A user-facing deck import error.</summary>
public sealed class DeckImportException(string message) : Exception(message);

/// <summary>Imports a public Moxfield deck or parses text exported by Moxfield.</summary>
public sealed partial class DeckImportService(IHttpClientFactory httpClientFactory)
{
    public async Task<ImportedDeck> ImportAsync(string? url, string? exportedText, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(exportedText)) return ParseText(exportedText, url);
        if (string.IsNullOrWhiteSpace(url)) throw new DeckImportException("Enter a Moxfield deck URL or paste a deck export.");
        if (!TryGetDeckId(url, out var deckId)) throw new DeckImportException("Enter a URL such as https://www.moxfield.com/decks/your-deck-id.");

        var client = httpClientFactory.CreateClient("Moxfield");
        using var response = await client.GetAsync($"v3/decks/all/{deckId}", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new DeckImportException(response.StatusCode switch
            {
                System.Net.HttpStatusCode.Forbidden => "Moxfield blocked automatic access to this deck. Use Moxfield's Export menu and paste the text below.",
                System.Net.HttpStatusCode.NotFound => "Moxfield could not find that public deck. Check the link or paste an export.",
                _ => $"Moxfield returned {(int)response.StatusCode}. You can paste a deck export instead."
            });
        }

        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = json.RootElement;
            var cards = new List<DeckEntry>();
            ReadBoard(root, "mainboard", cards);
            var commanders = new List<DeckEntry>();
            ReadBoard(root, "commanders", commanders);
            foreach (var commander in commanders)
                if (!cards.Any(card => card.Name.Equals(commander.Name, StringComparison.OrdinalIgnoreCase)))
                    cards.Add(commander);
            if (cards.Count == 0) throw new DeckImportException("Moxfield returned no main-deck cards. Paste its text export instead.");
            return new ImportedDeck(GetString(root, "name") ?? "Moxfield deck", url, Merge(cards));
        }
        catch (JsonException)
        {
            throw new DeckImportException("Moxfield returned an unreadable deck. Paste its text export instead.");
        }
    }

    public static bool TryGetDeckId(string url, out string deckId)
    {
        deckId = "";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Host is not ("www.moxfield.com" or "moxfield.com")) return false;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2 || parts[0] != "decks" || !DeckIdPattern().IsMatch(parts[1])) return false;
        deckId = parts[1];
        return true;
    }

    public static ImportedDeck ParseText(string text, string? url = null)
    {
        if (text.Length > 100_000) throw new DeckImportException("The deck export is too large.");
        var cards = new List<DeckEntry>();
        var includeSection = true;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('#') || line.EndsWith(':'))
            {
                var heading = line.TrimStart('/', '#', ' ').TrimEnd(':').ToLowerInvariant();
                includeSection = !heading.Contains("sideboard") && !heading.Contains("maybeboard")
                    && !heading.Contains("considering") && !heading.Contains("tokens");
                continue;
            }
            if (!includeSection) continue;
            var match = DeckLinePattern().Match(line);
            if (!match.Success) continue;
            var quantity = int.Parse(match.Groups[1].Value);
            var name = match.Groups[2].Value.Trim();
            // Moxfield text exports may include set codes and collector numbers.
            name = SetSuffixPattern().Replace(name, "").Trim();
            if (name.Length == 0 || quantity is < 1 or > 250) continue;
            cards.Add(new DeckEntry(name, quantity));
        }
        var merged = Merge(cards);
        if (merged.Count == 0) throw new DeckImportException("No cards were found. Paste a Moxfield text export with lines like '1 Sol Ring'.");
        if (merged.Sum(card => card.Quantity) > 250) throw new DeckImportException("A deck can contain at most 250 cards.");
        return new ImportedDeck("Imported deck", url, merged);
    }

    private static void ReadBoard(JsonElement root, string property, List<DeckEntry> cards)
    {
        if (!root.TryGetProperty(property, out var board) || board.ValueKind != JsonValueKind.Object) return;
        foreach (var item in board.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.Object) continue;
            var quantity = GetInt(item.Value, "quantity") ?? GetInt(item.Value, "count") ?? 1;
            var card = item.Value.TryGetProperty("card", out var nested) ? nested : item.Value;
            var name = GetString(card, "name") ?? item.Name;
            if (quantity > 0 && quantity <= 250 && !string.IsNullOrWhiteSpace(name)) cards.Add(new DeckEntry(name, quantity));
        }
    }

    private static List<DeckEntry> Merge(IEnumerable<DeckEntry> cards) => cards
        .GroupBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
        .Select(group => new DeckEntry(group.First().Name, group.Sum(card => card.Quantity)))
        .ToList();

    private static string? GetString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result)
        && result.ValueKind == JsonValueKind.String ? result.GetString() : null;

    private static int? GetInt(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.TryGetInt32(out var count) ? count : null;

    [GeneratedRegex("^[A-Za-z0-9_-]{6,64}$")]
    private static partial Regex DeckIdPattern();

    [GeneratedRegex("^(\\d{1,3})x?\\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DeckLinePattern();

    [GeneratedRegex("\\s+\\([A-Za-z0-9]{2,8}\\)\\s+\\d+[A-Za-z]?$|\\s+\\[[A-Za-z0-9]{2,8}\\].*$")]
    private static partial Regex SetSuffixPattern();
}
