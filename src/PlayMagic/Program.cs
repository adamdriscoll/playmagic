using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PlayMagic.Components;
using PlayMagic.Data;
using PlayMagic.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
var connectionString = builder.Configuration.GetConnectionString("PlayMagic");
if (string.IsNullOrWhiteSpace(connectionString))
{
    var dataDirectory = Path.Combine(builder.Environment.ContentRootPath, "Data");
    Directory.CreateDirectory(dataDirectory);
    connectionString = $"Data Source={Path.Combine(dataDirectory, "playmagic.db")}";
}
builder.Services.AddDbContextFactory<PlayMagicDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddSingleton<CardCatalogService>();
builder.Services.AddSingleton<GameNotifier>();
builder.Services.AddSingleton<GameService>();
builder.Services.AddHostedService<CatalogRefreshWorker>();
builder.Services.AddHostedService<GameCleanupWorker>();
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("create-game", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5, Window = TimeSpan.FromMinutes(10), QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.AddPolicy("join-game", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10, Window = TimeSpan.FromMinutes(10), QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter =
                Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        await context.HttpContext.Response.WriteAsJsonAsync(
            new { message = "Too many attempts. Please wait a few minutes and try again." }, cancellationToken);
    };
});
builder.Services.AddHttpClient("Scryfall", client =>
{
    client.BaseAddress = new Uri("https://api.scryfall.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PlayMagic/1.0 (local Magic tabletop app)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json;q=0.9, */*;q=0.8");
    client.Timeout = TimeSpan.FromMinutes(10);
});

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PlayMagicDbContext>();
    await db.Database.MigrateAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseRouting();
app.UseRateLimiter();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapPost("/api/games", async (CreateGameRequest request, GameService games) =>
{
    try { return Results.Ok(new { value = await games.CreateAsync(request.Format) }); }
    catch (GameActionException exception) { return Results.BadRequest(new { message = exception.Message }); }
}).RequireRateLimiting("create-game");
app.MapPost("/api/games/{id}/join", async (string id, JoinGameRequest request, GameService games) =>
{
    try
    {
        var token = await games.JoinAsync(id.ToUpperInvariant(), request.PlayerName ?? "",
            request.DeckText, request.DeckName);
        return Results.Ok(new { value = token });
    }
    catch (DeckImportException exception)
    {
        return Results.BadRequest(new { message = exception.Message });
    }
    catch (GameActionException exception) { return Results.BadRequest(new { message = exception.Message }); }
}).RequireRateLimiting("join-game");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
