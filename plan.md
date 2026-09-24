# Trakt My Year - Implementation Plan

## 1. Product Goal

Build a .NET 10 Web API that creates a year-in-review dataset from a user's Trakt account.
The first release will support movies and TV shows as whole titles. Individual episodes are out
of scope. A caller supplies a year such as `2026`; the API retrieves the relevant Trakt data,
stores it in an in-memory cache, and serves a normalized overview and sortable title lists.

The API should make it easy for a later presentation layer to answer questions such as:

- How many movies and shows were watched in a year?
- Which titles were watched, and when?
- Which titles were rated, and when were they rated?
- What are the best overall titles and possible "best obscure" titles?

The first slice is API-first. A browser frontend is a separate slice built on the API contract.

## Current Repository State

The repository now contains a working local vertical slice rather than only a proposal:

- The .NET 10 solution has API, application, domain, infrastructure, unit-test, and integration-test projects.
- Trakt OAuth authorization-code login uses PKCE, validates OAuth state, and supports connect, status,
  callback, and disconnect operations.
- The API imports watched movies and shows, maps ratings and timestamps, excludes episode records,
  applies a configured time zone, and caches normalized year reviews in memory.
- Versioned year-review endpoints support overviews, sorting, paging, refresh, deterministic top lists,
  and CSV exports.
- A static browser dashboard provides year selection, sortable movie/show tables, refresh, top lists,
  CSV downloads, and connection management.
- OpenAPI output, checked-in HTTP examples, and automated unit/integration test projects are present.

The current release remains intentionally local and single-user. Tokens and cached data are not
persistent, the frontend is served by the API, and no database or deployment configuration exists yet.

## 2. Scope and Acceptance Criteria

### Implemented in the current MVP

- Connect one Trakt account through OAuth 2.0 authorization-code flow.
- Securely retain the Trakt access token and refresh token for the local application.
- Query a valid calendar year between a configured minimum and maximum.
- Import watched movies and watched shows for that year.
- Import user ratings for movies and shows, preserving the rating timestamp where Trakt provides it.
- Exclude episode records from the public model and all aggregate counts.
- Return counts and normalized title records for movies and shows.
- Sort independently by:
  - title
  - rating, descending or ascending
  - watched moment, newest or oldest
  - rated moment, newest or oldest
- Cache Trakt responses and normalized year results in memory.
- Expose OpenAPI/Swagger UI and checked-in `.http` request examples.
- Provide automated unit, integration, contract, and smoke validation.

### Deferred

- SQL Server or another durable database.
- Multi-user tenancy and account switching.
- A separate presentation/frontend application; the current dashboard is served from the API.
- Individual episode history and episode-level counts.
- Recommendation algorithms, editorial scoring, and public sharing.
- Background synchronization and scheduled jobs.
- Deployment-specific identity management.

## 3. Recommended Solution Shape

Use the current .NET 10 ASP.NET Core Web API stack with nullable reference types, implicit usings,
OpenAPI generation, and the built-in dependency injection and configuration systems.

Current solution layout:

```text
src/
  TraktMyYear.Api/             HTTP endpoints, auth flow, Swagger, exception handling
  TraktMyYear.Application/     use cases, DTOs, validation, sorting, aggregation
  TraktMyYear.Domain/          title/year models and business rules
  TraktMyYear.Infrastructure/  Trakt client, OAuth, caching, serialization, clock
tests/
  TraktMyYear.UnitTests/
  TraktMyYear.IntegrationTests/
  TraktMyYear.ContractTests/
  src/TraktMyYear.Api/wwwroot/  static browser dashboard
```

Keep the application and domain layers independent of Trakt's response JSON and of the cache
implementation. The application layer should depend on interfaces such as `ITraktGateway`,
`IYearReviewRepository`, `ITokenStore`, and `IClock`. This makes the later SQL Server version an
implementation change rather than a rewrite of endpoint behavior.

## 4. Trakt Integration

Use the official Trakt API reference at <https://docs.trakt.tv/reference> as the source of truth.
Pin and send the required Trakt API headers, including the configured client ID, API version, and
content type as required by the current documentation.

### Authentication

1. Add a login endpoint that creates a cryptographically random `state` value and redirects to
   Trakt's OAuth authorization endpoint.
2. Add a callback endpoint that validates `state` before exchanging the authorization code.
3. Store access and refresh tokens through `ITokenStore`; never return them from the API or write
   them to logs.
4. Refresh an expired access token once, then retry the original request once. Avoid retry loops.
5. Make redirect URIs, client ID, client secret, and token protection configurable by environment.
6. For the first local version, document the required Trakt application setup and local callback
   URI. Do not commit secrets; use user secrets or environment variables.

### Data retrieval

Implement a typed `HttpClient` for Trakt with cancellation, timeouts, structured error handling,
rate-limit awareness, and pagination. Isolate the exact endpoint paths in the infrastructure layer.
The gateway should retrieve the user-scoped movie and show history and ratings needed to calculate
the year review, then map the responses into internal records.

Important normalization rules:

- Identify titles by Trakt ID and media type, not by title text.
- Preserve title, year, runtime when available, genres when available, poster/fanart URLs when
  available, rating, watched timestamp, and rated timestamp.
- Treat watched and rated dates as instants. Apply a documented timezone policy when assigning an
  item to a calendar year; default to UTC for deterministic API behavior.
- Deduplicate repeated watch history entries according to a documented rule. The MVP title count
  should count distinct movies/shows, while the model may retain watch count and last watched time.
- Never convert shows into episodes. A show is one title-level record.
- Keep missing ratings and missing dates as null rather than inventing values.

## 5. Cache Design

Use `IMemoryCache` for version one. The cache is an optimization and not the source of truth;
missing or expired entries must be re-fetchable from Trakt.

Suggested cache boundaries and keys:

- `trakt:profile:{account}` for the connected user's identity.
- `trakt:source:{account}:{resource}:{page}:{query}` for raw paginated API responses.
- `year-review:{account}:{year}:{sort}:{direction}` for the normalized query result.

Configure absolute and sliding expiration, maximum practical entry sizes, and a small jitter to
avoid simultaneous expiry. Use a per-key single-flight strategy so concurrent requests do not all
call Trakt for the same missing year. Add an explicit refresh/invalidate operation only if it is
needed by the API contract.

Define `ICacheStore` or an equivalent abstraction now, backed by `IMemoryCache`. The later SQL
version should be able to introduce durable snapshots without changing controllers or DTOs.

## 6. Public API Contract

Use versioned routes such as `/api/v1` and return `ProblemDetails` for errors.

Proposed endpoints:

- `GET /api/v1/auth/trakt/login` - start OAuth login.
- `GET /api/v1/auth/trakt/callback` - complete OAuth login.
- `GET /api/v1/auth/status` - report whether the local account is connected; no token data.
- `DELETE /api/v1/auth/trakt` - clear local token and cached account data.
- `GET /api/v1/year-reviews/{year}` - return the year overview and both title collections.
- `GET /api/v1/year-reviews/{year}/movies` - return movie records with sorting and pagination.
- `GET /api/v1/year-reviews/{year}/shows` - return show records with sorting and pagination.
- `POST /api/v1/year-reviews/{year}/refresh` - explicitly invalidate and rebuild the year cache,
  protected against accidental excessive use.

Query parameters for title lists:

- `sort=title|rating|watchedAt|ratedAt`
- `direction=asc|desc`
- `page` and `pageSize`

Return a stable envelope containing `year`, `generatedAt`, `totalCount`, paging metadata, and
items. The overview should include `movieCount`, `showCount`, `totalTitleCount`, watched/rated
counts, and the relevant collection timestamps. Keep the DTOs presentation-friendly without
embedding UI decisions or Trakt-specific JSON.

Define deterministic null sorting, tie-break by title then Trakt ID, maximum page size, invalid
year behavior, and behavior when no Trakt account is connected. Put these rules in the OpenAPI
description and tests.

## 7. Presentation-Oriented Extensions After MVP

Once the core dataset is reliable, add a separate analysis layer rather than baking heuristics
into basic list sorting.

- Best overall: configurable minimum rating and minimum number of ratings/watches.
- Best obscure: high personal or Trakt rating with low popularity, configurable by threshold.
- Most watched, most recently watched, and highest-rated unrated titles.
- Configurable ranking formulas with explainable score components.
- Export formats suitable for slides, such as JSON and CSV; consider a static report later.

All derived lists should include the rule/version used to calculate them so a presentation can be
reproduced after the scoring logic changes.

## 8. Configuration and Security

### Non-deferrable rule

Configuration and security are mandatory acceptance criteria, not deferred work. No slice may be
considered complete, merged, or released if it handles secrets, OAuth state, tokens, personal data,
transport security, upstream errors, or logging without the controls below. A feature that depends
on an unfinished security control must remain behind a disabled configuration flag or wait until
the control is implemented. Every slice's exit gate must include the applicable security tests and
a manual check that secrets are absent from responses and logs.

Configuration should include Trakt client settings, callback URL, cache durations, HTTP timeout,
year bounds, pagination limits, and logging options.

- Use .NET user secrets for local development and environment/secret-store configuration elsewhere.
- Do not commit API keys, client secrets, OAuth tokens, or real account data.
- Protect refresh tokens at rest where the host supports it; for local development, document the
  limitation clearly.
- Validate OAuth `state`, use HTTPS outside local development, and avoid putting tokens in URLs.
- Redact authorization headers, tokens, callback query values, and personal profile data from logs.
- Add response handling for Trakt `401`, `403`, `429`, and `5xx` responses with bounded retries and
  useful client-facing `ProblemDetails`.
- Keep the initial application single-user by design and document that limitation in the API.

## 9. Testing and Validation Gates

Every slice must pass `dotnet restore`, `dotnet build`, and `dotnet test` with warnings treated as
errors where practical.

### Unit tests

- Year filtering under the selected timezone policy.
- Movie/show separation and episode exclusion.
- Deduplication and watch-count/last-watched rules.
- Rating and watched/rated timestamp mapping, including nulls.
- Every supported sort and direction, deterministic tie-breaking, and null ordering.
- Paging boundaries and invalid query validation.
- Cache key construction, expiration policy, and single-flight behavior.
- OAuth state validation and token refresh decision logic.

### Integration tests

- Use `WebApplicationFactory` with a fake Trakt gateway and in-memory token/cache services.
- Verify status codes, `ProblemDetails`, authentication prerequisites, response envelopes, and
  endpoint routing.
- Verify repeated year requests use the cache and refresh bypasses it.
- Verify concurrent requests do not duplicate the upstream fetch.

### Contract and HTTP tests

- Check the generated OpenAPI document for required routes, parameters, schemas, and error shapes.
- Check in `.http` examples for login/status, year overview, movie/show sorting, pagination, and
  refresh.
- Add a fake HTTP server or recorded fixture tests for Trakt JSON and pagination; do not call the
  live Trakt API in CI.

### Manual smoke validation

After credentials are supplied, run the API locally, complete OAuth with a test Trakt account,
request one year, repeat the request to confirm a cache hit, try each sort, and test a year with no
matching titles. Verify logs contain no secrets.

## 10. Delivery Slices

### Slice 0 - Repository and baseline (complete)

- Create the .NET 10 solution and projects.
- Add analyzers, formatting rules, configuration templates, README setup notes, and CI validation.
- Add a health endpoint and a minimal OpenAPI document.

**Exit gate:** clean restore/build/test and the API starts locally.

### Slice 1 - Trakt authentication (complete for local use)

- Implement OAuth login/callback, protected token storage abstraction, auth status, and disconnect.
- Add fake-provider tests and local setup documentation.

**Exit gate:** a local test account can connect without secrets appearing in responses or logs.

### Slice 2 - Import and normalized year review (complete for local use)

- Implement typed Trakt client, pagination, token refresh, normalization, memory cache, and overview/list endpoints.
- Add fixtures for movies, shows, repeated watches, ratings, missing fields, and episode records.

**Exit gate:** all automated tests pass and a real account produces correct counts for one year.

### Slice 3 - API usability and analysis foundations (complete for local use)

- Finalize sorting/paging semantics, `.http` requests, OpenAPI descriptions, refresh behavior, and
  optional CSV export.
- Add initial configurable top-list queries only after the base data is trusted.

**Exit gate:** the API can provide repeatable inputs for a presentation without manual data cleanup.

### Slice 4 - SQL-ready persistence (next)

- Introduce a persistence port and SQL Server implementation behind the existing application
  interfaces.
- Store raw import metadata and normalized snapshots with schema migrations.
- Decide whether memory remains an L1 cache over SQL or is removed for selected deployments.

**Exit gate:** switching persistence implementations does not alter the public API contract or core
  aggregation tests.

### Slice 5 - Separate frontend (deferred)

- Build a separate frontend against the versioned API.
- Include year selection, overview metrics, movie/show tabs, sortable tables, title details, and
  presentation-friendly top lists.
- Add loading, empty, stale-cache, auth-required, rate-limit, and upstream-error states.

## 11. Decisions to Confirm Before Implementation

- Is the first release strictly one local Trakt account, or should account identity be modeled now?
- Should a title count once per year, while also exposing total watch plays?
- Should a title qualify for a year by watched date, rated date, or both, and should the API expose
  separate watched-year and rated-year views?
- Which timezone should define a calendar year for the user's Trakt timestamps?
- Should Trakt's rating be the default rating, or should personal rating and Trakt community rating
  be shown as separate fields?
- What does "obscure" mean for the first presentation: low popularity, low watch count, or a
  configurable combination?
- How long may cached data be considered fresh, and should refresh be manual only in the MVP?

## 12. Initial User Inputs Needed

Before Slice 1, collect:

- Trakt client ID.
- Trakt client secret, supplied through a secret channel and never committed.
- Chosen local callback URL.
- Preferred timezone for year boundaries.
- A test Trakt account with representative movie and show history.
