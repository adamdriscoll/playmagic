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
builder.Services.AddSingleton<DeckImportService>();
builder.Services.AddSingleton<GameNotifier>();
builder.Services.AddSingleton<GameService>();
builder.Services.AddHostedService<CatalogRefreshWorker>();
builder.Services.AddHttpClient("Scryfall", client =>
{
    client.BaseAddress = new Uri("https://api.scryfall.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PlayMagic/1.0 (local Magic tabletop app)");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json;q=0.9, */*;q=0.8");
    client.Timeout = TimeSpan.FromMinutes(10);
});
builder.Services.AddHttpClient("Moxfield", client =>
{
    client.BaseAddress = new Uri("https://api2.moxfield.com/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PlayMagic/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.Timeout = TimeSpan.FromSeconds(20);
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
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
