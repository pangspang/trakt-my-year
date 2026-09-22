# Trakt My Year

## Slice 1: Trakt authentication

The first slice is a .NET 10 Web API that implements the local Trakt OAuth flow.

1. Keep the local `.env` file populated with the Trakt client settings. It is ignored by Git.
2. Start the API with `dotnet run --project src/TraktMyYear.Api`. In Development, it opens
	the local Trakt login endpoint in the default browser automatically.
3. If the browser does not open, visit `http://localhost:5000/api/v1/auth/trakt/login` manually.
4. Approve access in Trakt. Trakt redirects to the configured callback URL.
5. After a successful login, the browser lands on a local page with links for the auth status,
   year review, movie, show, refresh, and disconnect endpoints. Each request response is shown on
   the page. The checked-in `.http` requests remain available for API-only use.

The callback URL registered in Trakt must exactly match `TRAKT_CALLBACK_URL`. Slice 1 stores the
token only in memory, so restarting the API requires connecting the account again. The access and
refresh tokens are never returned by the API.

OpenAPI JSON is available at `/openapi/v1.json` while running in the Development environment.

## Slice 2: year review API

After connecting Trakt, request the year overview and title lists:

- `GET /api/v1/year-reviews/2026`
- `GET /api/v1/year-reviews/2026/movies?sort=rating&direction=desc`
- `GET /api/v1/year-reviews/2026/shows?sort=watchedAt&direction=desc`
- `POST /api/v1/year-reviews/2026/refresh`

The first request imports watched history and ratings into an in-memory year snapshot. Subsequent
requests reuse that snapshot for 30 minutes, with a 10-minute sliding expiration. A refresh rebuilds
the selected year. Show history is represented as title-level records; episode records are never
returned by the API. The configured `TRAKT_TIMEZONE` controls the calendar-year boundary.

## Slice 3: presentation exports and top lists

The API supports repeatable presentation inputs without manual cleanup:

- `GET /api/v1/year-reviews/2026/movie/top?limit=10&minimumRating=8`
- `GET /api/v1/year-reviews/2026/show/top?limit=10`
- `GET /api/v1/year-reviews/2026/movie/export.csv?sort=rating&direction=desc`

Top lists use personal rating when present, then Trakt rating, watch count, title, and Trakt ID
as deterministic tie-breakers. `limit` is 1-100 and `minimumRating` is 0-10. CSV exports contain
the complete cached collection for the selected media type. The browser page provides the same
operations with year selection, sortable tables, top lists, refresh, and downloads.