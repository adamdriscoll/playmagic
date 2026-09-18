using System.Text.RegularExpressions;

namespace PlayMagic.Services;

/// <summary>A card and the number of copies in an imported deck.</summary>
public sealed record DeckEntry(string Name, int Quantity, string? SetCode = null, string? CollectorNumber = null);

/// <summary>A deck ready to turn into physical game cards.</summary>
public sealed record ImportedDeck(string Name, IReadOnlyList<DeckEntry> Cards);

/// <summary>A user-facing deck import error.</summary>
public sealed class DeckImportException(string message) : Exception(message);

/// <summary>Parses pasted plain text deck lists from common deck builders.</summary>
public sealed partial class DeckImportService
{
    public static ImportedDeck ParseText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new DeckImportException("Paste a deck list with lines like '1 Sol Ring'.");
        if (text.Length > 100_000) throw new DeckImportException("The deck list is too large.");
        var cards = new List<DeckEntry>();
        var includeSection = true;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('#') || line.EndsWith(':')
                || SectionPattern().IsMatch(line))
            {
                var heading = line.TrimStart('/', '#', ' ').TrimEnd(':').ToLowerInvariant();
                includeSection = !heading.Contains("sideboard") && !heading.Contains("maybeboard")
                    && !heading.Contains("considering") && !heading.Contains("tokens")
                    && !heading.Contains("companion") && !heading.Contains("outside the game");
                continue;
            }
            if (!includeSection || line.StartsWith("SB:", StringComparison.OrdinalIgnoreCase)) continue;
            var match = DeckLinePattern().Match(line);
            if (!match.Success) continue;
            var quantity = int.Parse(match.Groups[1].Value);
            var name = match.Groups[2].Value.Trim();
            if (ExcludedCategoryPattern().IsMatch(name)) continue;
            name = ArchidektCategorySuffixPattern().Replace(name, "").Trim();
            string? setCode = null;
            string? collectorNumber = null;
            // Keep the printing ID for alternate card names that the Oracle catalog does not use.
            var printing = PrintingSuffixPattern().Match(name);
            if (printing.Success)
            {
                setCode = printing.Groups["set"].Value;
                collectorNumber = printing.Groups["collector"].Success ? printing.Groups["collector"].Value : null;
                name = name[..printing.Index].Trim();
            }
            else name = BracketSetSuffixPattern().Replace(name, "").Trim();
            name = FoilSuffixPattern().Replace(name, "").Trim();
            name = name.Replace(" / ", " // ", StringComparison.Ordinal);
            if (name.Length == 0 || quantity is < 1 or > 250) continue;
            cards.Add(new DeckEntry(name, quantity, setCode, collectorNumber));
        }
        var merged = Merge(cards);
        if (merged.Count == 0) throw new DeckImportException("No cards were found. Paste a deck list with lines like '1 Sol Ring'.");
        if (merged.Sum(card => card.Quantity) > 250) throw new DeckImportException("A deck can contain at most 250 cards.");
        return new ImportedDeck("Imported deck", merged);
    }

    private static List<DeckEntry> Merge(IEnumerable<DeckEntry> cards) => cards
        .GroupBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
        .Select(group =>
        {
            var printing = group.FirstOrDefault(card => card.SetCode is not null) ?? group.First();
            return new DeckEntry(group.First().Name, group.Sum(card => card.Quantity),
                printing.SetCode, printing.CollectorNumber);
        })
        .ToList();

    [GeneratedRegex("^(?:deck|mainboard|commander|companion|sideboard|maybeboard|considering|tokens|outside the game)(?:\\s*\\(\\d+\\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex SectionPattern();

    [GeneratedRegex("^(\\d{1,3})\\s*x?\\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DeckLinePattern();

    [GeneratedRegex("\\s+\\((?<set>[A-Za-z0-9]{2,8})\\)(?:\\s+(?<collector>[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*))?(?:\\s+\\*[A-Za-z]+\\*)*$")]
    private static partial Regex PrintingSuffixPattern();

    [GeneratedRegex("\\s+\\[[A-Za-z0-9]{2,8}\\].*$")]
    private static partial Regex BracketSetSuffixPattern();

    [GeneratedRegex("\\[(?:sideboard|maybeboard|considering|tokens)\\]", RegexOptions.IgnoreCase)]
    private static partial Regex ExcludedCategoryPattern();

    [GeneratedRegex("\\s+\\[[^\\]]+\\](?:\\s+\\^[^\\^]*\\^)?$")]
    private static partial Regex ArchidektCategorySuffixPattern();

    [GeneratedRegex("(?:\\s+\\*[A-Za-z]+\\*)+$")]
    private static partial Regex FoilSuffixPattern();
}
