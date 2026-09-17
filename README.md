# PlayMagic

A small, account-free Magic tabletop built with ASP.NET Core 10, Blazor interactive server components, EF Core, and SQLite.

## Run

```powershell
dotnet run --project src/PlayMagic/PlayMagic.csproj --launch-profile https
```

Open the URL printed by `dotnet run`. On first startup the app downloads Scryfall's English Oracle Cards bulk file and indexes it in `src/PlayMagic/Data/playmagic.db`. The home page can show a live random card while that first import runs. A background worker checks for a new bulk file every 12 hours and refreshes the local catalog once it is a week old. Card images stay on Scryfall's image host.

The default SQLite path is under the app's `Data` directory. Set `ConnectionStrings__PlayMagic` to use another SQLite connection string. Schema migrations run at startup.

## Play

1. Create a Commander or Regular game on the home page.
2. Share the eight-character code or game URL. Up to four people can join.
3. Each player enters a name and public Moxfield deck URL. If Moxfield blocks automatic access, paste the plain text list from Moxfield's **Export** menu instead.
4. Each deck is shuffled and starts with seven cards in hand. Drag cards among your hand, battlefield, graveyard, exile, and library drop areas. Click a card to tap it, move it, or change named counters. Use the player controls for life, poison or other named counters, drawing, and shuffling.

Hands are private to their owning browser; only hand counts are shown to opponents. An anonymous seat token is stored in that browser's local storage, so clearing browser storage loses access to that seat. Game state remains in SQLite across server restarts. Live updates currently assume one app server instance.

The tabletop does not enforce Magic rules, turn stages, commander zones, or card abilities. Sideboard and maybeboard cards are left out of text imports.

Moxfield does not offer a supported public deck API. The URL importer uses its public deck endpoint, which may return HTTP 403; the text export path is the reliable fallback.

## Verify

```powershell
dotnet test PlayMagic.slnx
```

The tests cover four-seat limits, private hands, card ownership, and text import sections.

## VS Code and CI

Open the repository root in VS Code and select **PlayMagic (HTTPS)** in Run and Debug. F5 restores and builds the solution, starts the web app, and opens its URL. **Terminal → Run Task** also offers `restore`, `build`, `build: Release`, `test`, and `run`. The C# extension is recommended by the workspace.

The GitHub Actions workflow restores, builds in Release mode, and runs the tests on pushes and pull requests.
