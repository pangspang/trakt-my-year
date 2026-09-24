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
DELETE /api/v1/auth/trakt
```

The same requests are available in [TraktMyYear.Api.http](src/TraktMyYear.Api/TraktMyYear.Api.http).
The first year request imports watched history and ratings into an in-memory snapshot. Snapshots
expire after 30 minutes with a 10-minute sliding expiration; refresh explicitly rebuilds the year.
CSV exports contain the complete cached collection for the selected media type.

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