using System.IO.Compression;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlayMagic.Data;

namespace PlayMagic.Services;

/// <summary>Downloads Scryfall's English Oracle catalog and serves local card queries.</summary>
public sealed class CardCatalogService(
    IDbContextFactory<PlayMagicDbContext> contextFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<CardCatalogService> logger)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public async Task<(int Count, DateTimeOffset? Updated)> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var sync = await db.CatalogSyncs.AsNoTracking().FirstOrDefaultAsync(item => item.Id == 1, cancellationToken);
        return (sync?.CardCount ?? 0, sync?.LastUpdatedUtc);
    }

    public async Task<CatalogCard?> GetRandomAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var count = await db.Cards.CountAsync(cancellationToken);
        if (count > 0)
        {
            return await db.Cards.AsNoTracking().OrderBy(card => card.Id)
                .Skip(Random.Shared.Next(count)).FirstOrDefaultAsync(cancellationToken);
        }

        // The home page remains useful while the first bulk download is in progress.
        try
        {
            var client = httpClientFactory.CreateClient("Scryfall");
            using var response = await client.GetAsync("cards/random?q=lang%3Aen", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return ReadCard(json.RootElement);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException)
        {
            logger.LogWarning(exception, "Could not load a temporary random card from Scryfall");
            return null;
        }
    }

    public async Task<List<CatalogCard>> SearchAsync(string term, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(term)) return [];
        term = term.Trim();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Cards.AsNoTracking()
            .Where(card => EF.Functions.Like(card.Name, $"%{EscapeLike(term)}%", "\\"))
            .OrderBy(card => card.Name).Take(24).ToListAsync(cancellationToken);
    }

    public async Task<CatalogCard?> FindByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var exact = await db.Cards.AsNoTracking().FirstOrDefaultAsync(card => card.Name == name, cancellationToken);
        if (exact is not null) return exact;
        return await db.Cards.AsNoTracking().FirstOrDefaultAsync(
            card => EF.Functions.Like(card.Name, $"{EscapeLike(name)} // %", "\\"), cancellationToken);
    }

    public async Task RefreshIfDueAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var status = await GetStatusAsync(cancellationToken);
            if (status.Updated is not null && DateTimeOffset.UtcNow - status.Updated < TimeSpan.FromDays(7)) return;

            var client = httpClientFactory.CreateClient("Scryfall");
            using var metadataResponse = await client.GetAsync("bulk-data/oracle-cards", cancellationToken);
            metadataResponse.EnsureSuccessStatusCode();
            await using var metadataStream = await metadataResponse.Content.ReadAsStreamAsync(cancellationToken);
            using var metadata = await JsonDocument.ParseAsync(metadataStream, cancellationToken: cancellationToken);
            var uri = metadata.RootElement.GetProperty("jsonl_download_uri").GetString();
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var downloadUri)
                || downloadUri.Scheme != "https" || downloadUri.Host != "data.scryfall.io")
                throw new InvalidOperationException("Scryfall returned an unexpected bulk download URL.");

            logger.LogInformation("Downloading Oracle cards from {DownloadUri}", downloadUri);
            using var response = await client.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var gzip = new GZipStream(responseStream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);

            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Cards.ExecuteDeleteAsync(cancellationToken);
            var batch = new List<CatalogCard>(500);
            var count = 0;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                using var cardJson = JsonDocument.Parse(line);
                var card = ReadCard(cardJson.RootElement);
                if (card is null) continue;
                batch.Add(card);
                if (batch.Count < 500) continue;
                db.Cards.AddRange(batch);
                await db.SaveChangesAsync(cancellationToken);
                count += batch.Count;
                batch.Clear();
                db.ChangeTracker.Clear();
            }
            if (batch.Count > 0)
            {
                db.Cards.AddRange(batch);
                await db.SaveChangesAsync(cancellationToken);
                count += batch.Count;
                db.ChangeTracker.Clear();
            }
            if (count < 10000) throw new InvalidOperationException($"Oracle catalog contained only {count} English cards.");
            var sync = await db.CatalogSyncs.FirstOrDefaultAsync(item => item.Id == 1, cancellationToken);
            if (sync is null)
                db.CatalogSyncs.Add(new CatalogSync { Id = 1, LastUpdatedUtc = DateTimeOffset.UtcNow, CardCount = count });
            else
            {
                sync.LastUpdatedUtc = DateTimeOffset.UtcNow;
                sync.CardCount = count;
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Loaded {CardCount} English Oracle cards", count);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static CatalogCard? ReadCard(JsonElement value)
    {
        if (GetString(value, "lang") != "en") return null;
        var id = GetString(value, "id");
        var name = GetString(value, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) return null;
        var image = ReadImage(value);
        string? backImage = null;
        if (value.TryGetProperty("card_faces", out var faces) && faces.ValueKind == JsonValueKind.Array)
        {
            if (image is null && faces.GetArrayLength() > 0) image = ReadImage(faces[0]);
            if (faces.GetArrayLength() > 1) backImage = ReadImage(faces[1]);
        }
        return new CatalogCard
        {
            Id = id,
            OracleId = GetString(value, "oracle_id") ?? id,
            Name = name,
            TypeLine = GetString(value, "type_line") ?? "",
            ManaCost = GetString(value, "mana_cost") ?? "",
            OracleText = GetString(value, "oracle_text") ?? "",
            ImageUrl = image,
            BackImageUrl = backImage
        };
    }

    private static string? ReadImage(JsonElement value)
    {
        if (!value.TryGetProperty("image_uris", out var imageUris)) return null;
        var url = GetString(imageUris, "normal") ?? GetString(imageUris, "large");
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https"
            && uri.Host == "cards.scryfall.io" ? url : null;
    }

    private static string? GetString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
            ? result.GetString() : null;
}

/// <summary>Checks for a weekly catalog update without delaying application startup.</summary>
public sealed class CatalogRefreshWorker(CardCatalogService catalog, ILogger<CatalogRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await catalog.RefreshIfDueAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                logger.LogError(exception, "Card catalog refresh failed; retrying in one hour");
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
    }
}
