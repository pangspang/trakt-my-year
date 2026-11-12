# Trakt My Year

Trakt My Year is a local .NET 10 web application that turns a Trakt account's movie and show
history into a year-in-review dataset. It includes a browser view, a versioned HTTP API, CSV
exports, and OpenAPI output. Episodes are intentionally represented as title-level show records.

This project is currently designed for one local Trakt account. Data and OAuth tokens are held in
memory and are lost when the application stops.

## Requirements

- .NET 10 SDK
- A Trakt account
- A Trakt API application at <https://trakt.tv/oauth/applications>

The repository contains both the API and test projects in `TraktMyYear.slnx`.

## Trakt application setup

Create a Trakt application and set its redirect URI to exactly:

`http://localhost:5000/api/v1/auth/trakt/callback`

The redirect URI must match `TRAKT_CALLBACK_URL` exactly, including the scheme, port, path, and
trailing slash behavior.

## Configuration

The application automatically loads a `.env` file from the repository root or a parent directory.
`.env` is ignored by Git. Create one locally with the following values:

```dotenv
TRAKT_CLIENT_ID=your-trakt-client-id
TRAKT_CLIENT_SECRET=your-trakt-client-secret
TRAKT_CALLBACK_URL=http://localhost:5000/api/v1/auth/trakt/callback
TRAKT_TIMEZONE=UTC
```

`TRAKT_CLIENT_ID` and `TRAKT_CALLBACK_URL` are required. `TRAKT_CLIENT_SECRET` is accepted for
configuration compatibility but the current authorization-code flow uses PKCE and does not send
the secret. Never commit real credentials, tokens, or account data.

Optional variables:

| Variable | Default | Purpose |
| --- | --- | --- |
| `TRAKT_TIMEZONE` | `UTC` | Time zone used to determine calendar-year boundaries. |
| `TRAKT_API_BASE_URL` | `https://api.trakt.tv` | Trakt API base URL. |
| `TRAKT_TEST_ACCOUNT` | `me` | Trakt username used by the upstream client. |

Environment variables supplied by the shell take effect as well. The local `.env` file is intended
for development only.

## Run locally

From the repository root:

```powershell
dotnet restore TraktMyYear.slnx
dotnet run --project src/TraktMyYear.Api
```

The Development profile listens on `http://localhost:5000` and opens the Trakt login flow in the
default browser. If it does not open, visit:

<http://localhost:5000/api/v1/auth/trakt/login>

After authorization, open <http://localhost:5000/> for the browser dashboard. OpenAPI JSON is
available at <http://localhost:5000/openapi/v1.json> in Development.

## API examples

Connect the account before requesting year data:

```http
GET /api/v1/auth/status
GET /api/v1/year-reviews/2026
GET /api/v1/year-reviews/2026/movies?sort=rating&direction=desc
GET /api/v1/year-reviews/2026/shows?sort=watchedAt&direction=desc
POST /api/v1/year-reviews/2026/refresh
GET /api/v1/year-reviews/2026/movie/top?limit=10&minimumRating=8
GET /api/v1/year-reviews/2026/show/top?limit=10
GET /api/v1/year-reviews/2026/movie/export.csv?sort=rating&direction=desc
GET /api/v1/year-reviews/2026/movie/export.csv?format=simkl
GET /api/v1/year-reviews/2026/show/export.csv?format=simkl
DELETE /api/v1/auth/trakt
```

The same requests are available in [TraktMyYear.Api.http](src/TraktMyYear.Api/TraktMyYear.Api.http).
The first year request imports watched history and ratings into an in-memory snapshot. Snapshots
expire after 30 minutes with a 10-minute sliding expiration; refresh explicitly rebuilds the year.
Default CSV exports contain the complete cached collection for the selected media type.

## Simkl CSV export

Choose **Simkl CSV** in the movie or show export selector, then **Download CSV**, or add
`format=simkl` to the existing export endpoint. The default format (`format=csv`) is unchanged.
Both formats export all matching titles, independently of table pagination or top-list limits.

The Simkl export reads fresh, paginated Trakt history for the selected calendar year using the
configured time zone. It counts each movie/show once and keeps its latest watch within that year.
Episode events are grouped into shows internally, not returned as episode rows. Titles currently
rated in the selected year are also included, with no invented watch date or completion status.
Current personal ratings enrich included titles even when rated outside the selected year; unrelated
titles rated outside the year are not added. This is not a historical snapshot of past rating values.
The normal dashboard and default CSV keep their existing cached review semantics, so a title
rewatched in a later year can appear in the earlier year's Simkl export but not its dashboard.

Files use Simkl's 13 documented columns, with movie/TV type, available IMDb/TMDB/TVDB IDs, title,
release year, UTC watch date (`DD-MM-YYYY`), and personal integer rating (1-10). Watched movies
are marked `completed`. For shows, `LastEpWatched` contains the season and episode from the
latest watch event in the selected year, formatted as `S04E08` (specials use season `00`). This
is latest by watch time, not the highest episode number or a guarantee of contiguous progress.
Movies, rating-only shows, and unknown episode numbers leave this field blank. Missing optional
values remain blank. `SIMKL_ID`, `MAL_ID`, and `Memo` are intentionally blank; show `Watchlist`
is also blank because a watch does not establish completion. No external ID lookups or private-note
requests are made. Trakt IDs are never used as Simkl IDs.

Each UTF-8 CSV is limited to 4,900,000 bytes, conservatively below Simkl's advertised 5 MB limit.
Large downloads return a ZIP archive: extract it and import each CSV part separately. Each part
has its own header and complete records; a single record exceeding the limit fails the export.
Downloads are marked `Cache-Control: no-store`; no data is automatically uploaded to Simkl.

At <https://simkl.com/apps/import/csv/>, select **Use .csv data** rather than overriding every
row's status. Manual validation in a disposable Simkl account is still required, especially for
blank show status, rating-only rows, and repeated imports. The importer is not a full watch-event
or rewatch migration tool. Do not clear an existing Simkl library merely to test an export.

**Import limitation:** Movie exports work well for transferring movie titles, watch dates, and
ratings. Series episode import is only partial: `LastEpWatched` records a single latest episode
marker, not the complete set of watched episodes. Simkl may therefore not restore series progress
or episode history exactly, especially when episodes were watched out of order or there are gaps.
Treat the series CSV as a best-effort title/progress hint, not a complete episode migration.

Title text is preserved, including formula-like prefixes. CSV quoting does not prevent spreadsheet
formula execution: import such columns as plain text in spreadsheet tools, and treat personal
exports as private data. Do not commit generated files or send them to logs.

## Test and build

```powershell
dotnet build TraktMyYear.slnx
dotnet test TraktMyYear.slnx
```

Tests use fakes and do not require live Trakt credentials.

## Public repository notes

- Do not commit `.env`, Trakt client credentials, OAuth tokens, or personal exports.
- This is a single-user local application, not a hosted multi-user service.
- Tokens are stored only in process memory and are cleared on restart or disconnect.
- Use HTTPS and a protected secret store before exposing the application beyond localhost.

## License

This project is available under the [MIT License](LICENSE).