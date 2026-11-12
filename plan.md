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

### Slice 3a - Simkl-compatible CSV export (implemented; manual import gate pending)

**Goal:** add an additional CSV format that can be imported at
<https://simkl.com/apps/import/csv/>, filling every field supported by reliable source data.
Keep the existing spreadsheet/presentation CSV unchanged.

**Implementation status (2026-11-06):** the API and dashboard now support `format=simkl`,
external IDs are preserved through the gateway and normalized records, and a separate export
source uses complete selected-year history plus current ratings. The default cached review and
spreadsheet CSV retain their existing semantics. All 13 headers are emitted; `LastEpWatched`
uses the episode from the latest show watch within the selected year, formatted as `S04E08`.
Unknown episodes, show completion status, personal notes, and unsupported IDs remain blank. Large exports
download as a ZIP with independently importable parts capped at 4,900,000 UTF-8 bytes each.
Automated parser, mapping, pagination, service, and real-HTTP tests cover the implemented path.
Live Trakt contract confirmation and manual import into a disposable Simkl account remain release
gates; no live account import has been performed or claimed.

#### Format and evidence

- Simkl's importer documents the 13 headers below, in any order; unknown columns are ignored.
  All columns are optional, but each exported row must have useful identification: prefer an
  external ID, with title and release year as fallback. A Trakt ID is not a Simkl ID.
- `Type` accepts `movie`, `tv`, or `anime`. `TMDB_ID` requires `Type` because movie and TV IDs
  share a numeric namespace. Simkl searches anime too when a `tv` title is not found in TV.
- `Watchlist` accepts `watching`, `plan to watch`, `completed`, `dropped`, or `on hold`.
  `Rating` is a personal score from 1 to 10. `WatchedDate` is UTC; use the documented
  date-only `DD-MM-YYYY` form initially, rather than assuming ISO 8601 is accepted.
- The upload limit is 5 MB. Simkl warns that its CSV backup retains only the last episode
  number, not individual episode history or gaps. Treat CSV as a lossy title/progress transfer,
  not a complete migration of watch events or rewatches.
- Sources checked on 2026-11-06: the Simkl page above and the repository's gateway, normalized
  records, merge logic, and existing CSV controller. Trakt reference entry point:
  <https://docs.trakt.tv/reference>. The attempted watched/ratings/history documentation URLs
  redirected to unavailable pages. Synthetic fixtures validate implemented request paths and
  response mapping; live confirmation against the current official contracts remains a release gate.

#### Trakt API -> Trakt My Year -> Simkl cross-reference

Paths below are response paths; `movie|show` means the matching title object in watched or
ratings responses. Current reads use `/users/{username}/watched/{movies|shows}?extended=full`
and `/users/{username}/ratings/{movies|shows}?extended=full`. Simkl export additionally reads
`/users/{username}/history/{movies|shows}` with `start_at`, `end_at`, `extended=full`, and complete
pagination. Shared identifiers now use nullable `TitleExternalIds` on both gateway records and
`YearReviewItem`; export history aggregation and snapshot merges preserve IDs and reject conflicts.

| Trakt API source | Current Trakt My Year model / proposed extension | Simkl CSV column | Mapping and gap / action |
| --- | --- | --- | --- |
| Movie/show endpoint or response type | `MediaType` on `TraktWatchedTitle`, `TraktRatedTitle`, and `YearReviewItem` | `Type` | `Movie` -> `movie`; `Show` -> `tv`. Do not guess `anime` from genres; use Simkl's documented TV/anime fallback. Available now. |
| No Simkl ID in the currently consumed Trakt data | No member; optional future `SimklId` | `SIMKL_ID` | Blank unless an authoritative cross-service lookup is added. Never substitute `TraktId`. External lookups are provisionally out of scope. |
| `show.ids.tvdb` where returned | `ExternalIds.Tvdb` through gateway parsing, both source records, and `YearReviewItem` | `TVDB_ID` | Implemented: preserve the show-level ID; leave missing values blank. Never use an episode TVDB ID for a show row. |
| `movie.ids.tmdb` / `show.ids.tmdb` | `ExternalIds.Tmdb` through the same pipeline | `TMDB_ID` | Implemented: export numeric ID with the corresponding `Type`; blank when absent. |
| `movie.ids.imdb` / `show.ids.imdb` | Nullable string `ExternalIds.Imdb` through the same pipeline | `IMDB_ID` | Implemented: preserve the full `tt...` string, including leading zeroes; blank when absent. |
| No MAL ID in the currently consumed Trakt data | No member; optional future `MalId` | `MAL_ID` | Blank unless a verified upstream field or authoritative external mapping is available. Do not derive from Trakt/TMDB IDs or title matching alone. |
| `movie.title` / `show.title` | `Title` on both source records and `YearReviewItem` | `Title` | Available now. Preserve Unicode and escape commas, quotes, and line breaks correctly. |
| `movie.year` / `show.year` | Nullable `Year` on both source records and `YearReviewItem` | `Year` | Release year, not the selected review year. Available now; blank when unknown. |
| History `episode.season` / `episode.number`, selected by latest `watched_at` within the year | Export `YearReviewItem.LastEpisodeWatched` stores a typed `WatchedEpisode(Season, Number)` alongside `WatchedAt` | `LastEpWatched` | Implemented: format `S04E08` with at least two digits; specials use `S00`. Latest by watch time, not highest episode number. Blank for movies, rating-only shows, or missing/invalid episode numbers; not proof of contiguous progress. |
| Movie watch evidence; show watched progress; watchlist membership and paused/hidden state would require separate verified sources | `WatchedAt` supplies watch evidence only; proposed nullable export `WatchlistStatus` | `Watchlist` | Watched movie -> `completed`. A watched show is not necessarily completed; rating alone proves neither watched nor planned. Show completion, ongoing/partial progress, and explicit list states need verified rules. Leave uncertain status blank rather than inventing intent. |
| History `watched_at` for Simkl export; watched response `last_watched_at` remains in the normal review | Export-only latest-in-year `YearReviewItem.WatchedAt` | `WatchedDate` | Implemented: convert instant to UTC, then format `dd-MM-yyyy` invariantly. Blank for rating-only rows. History resolves the later-rewatch issue for Simkl exports only. Date-only export intentionally loses time of day. |
| Ratings response `rating` and `rated_at` | `TraktRatedTitle.PersonalRating` / `RatedAt` -> export `YearReviewItem.PersonalRating` / `RatedAt` | `Rating` | Implemented: current integer personal ratings 1-10, without rescaling; invalid/fractional values fail export. Missing stays blank. Current ratings enrich only included titles; see G3. Simkl has no rated-date column. |
| Personal title notes, only if supported by a verified Trakt notes endpoint and account permissions | No personal-note member; `Overview` is a synopsis, not a user note; proposed export-only `Memo` | `Memo` | Prefer the user's own note if retrieval is supported and approved. Do not substitute synopsis, public comments, links, or generated text. Otherwise blank; verify note visibility, length, and multiline handling. |

Other existing fields (`TraktId`, `RatedAt`, `PosterUrl`, `TraktUrl`, and `Overview`) have no
dedicated Simkl column. Keep them in the existing export; do not overload `Memo` to carry them.

#### Gaps and implementation work

| Gap | Work and decision |
| --- | --- |
| G1 - External identifiers (implemented) | `TraktGateway` parsing, both source records, and `YearReviewItem` now preserve nullable `TitleExternalIds` for IMDb/TMDB/TVDB. Watched/rated metadata merges reject conflicting IDs. Parser and serializer fixtures cover absent IDs, string IMDb IDs, non-null merges, and conflicts. |
| G2 - Export year membership (implemented) | Default watched endpoints still filter all-time `last_watched_at`, so a later rewatch can hide an earlier-year watch in the normal review. Simkl export independently retrieves complete time-bounded history, deduplicates titles, and keeps the latest watch within the selected year. Episode events become show rows only. Fixtures cover year boundaries, repeated watches, specials, out-of-order episodes, and more than 100 pages. Changing the normal review's membership is outside this slice. |
| G3 - Ratings coverage (implemented) | The normal review retains selected-year rating semantics. Export reads all current ratings, enriches included watched titles even when rated outside the year, and adds rating-only titles only when their current `rated_at` is in the year. No unrelated titles are added. This cannot reconstruct historical rating changes and is explicitly documented as current-rating enrichment. |
| G4 - Last watched episode (implemented); completion status deferred | Export retains `episode.season` and `episode.number` from the latest-in-year show history event and serializes `LastEpWatched` as `S04E08`. Specials use season `00`; missing/invalid episode numbers stay blank. Out-of-order history is resolved by watch timestamp, not highest episode number. This does not reconstruct all-time or contiguous progress; show status stays blank, and Simkl's treatment of gaps still needs manual verification. |
| G5 - Notes and unsupported IDs | Verify Trakt personal-note API availability and permission requirements before committing to `Memo` enrichment; otherwise leave blank. Do not fetch public comments as private notes. Provisionally omit external Simkl/MAL lookups to avoid another credential, dependency, and ambiguous matching. Missing optional fields are legitimate, not values to invent. |
| G6 - Export contract and UI (implemented) | `?format=simkl` uses the new application export service; `format=csv` and the omitted default preserve spreadsheet output. Dashboard selectors, OpenAPI metadata, HTTP examples, unpaged downloads, distinct filenames, and invalid-parameter checks are implemented and covered by real-HTTP tests. |
| G7 - CSV compatibility and limits (implemented) | All 13 headers use UTF-8, commas, CRLF, invariant whole-number ratings, UTC dates, doubled quotes, and empty null cells. CSV parts are limited to 4,900,000 bytes, below either common interpretation of 5 MB, and split only at complete records with a header in every part. Multiple parts download in a ZIP. Tests cover Unicode byte sizes, multiline text, oversized records, header-only output, and ZIP contents. Formula-like text is preserved; spreadsheet risk is documented. |
| G8 - Import semantics and privacy | Verify how Simkl handles blank `Watchlist`, rating-only rows, existing items, last-episode gaps, repeated imports, and UTC date parsing using synthetic data in a disposable account. These behaviors are not specified by the linked format table. Use the importer's per-row CSV mode rather than a global status override during verification. Do not log exported viewing history/notes or upload them automatically; export remains a user-initiated local download. |

#### Selected defaults and remaining questions

Implementation follows the conservative defaults below. The remaining enrichment choices must
be resolved and verified before populating currently blank fields:

1. **Year or full library?** Recommend the selected year, matching the existing export workflow,
   including watched-in-year or rated-in-year titles. A full-library migration is a separate option
   requiring an explicit API/UI scope and complete traversal, not removal of one date filter.
2. **Episode enrichment allowed?** Export now reads episode history internally to establish show
  year membership, while public collections/counts remain title-level. It does not retrieve or
  claim all-time contiguous progress. `LastEpWatched` now identifies the latest episode watched
  within the selected year; show status stays blank.
3. **Historical or current enrichment?** Implemented selected-year `WatchedDate` with current
  personal ratings. All-time show progress remains deferred. Historical rating values cannot be
  recovered from the current ratings endpoint; this is documented in the API and README.
4. **How should partial show progress import?** Per the user's follow-up, populate `LastEpWatched`
  from the latest-by-time episode in the selected year, keeping it aligned with `WatchedDate`.
  Do not infer completion. Simkl may interpret the value as progress across earlier episodes;
  verify gap handling manually before using this as a full migration.
5. **How much optional enrichment?** Recommend Trakt-only identifiers and personal notes where
   available; blank `SIMKL_ID`/`MAL_ID`. Confirm whether personal notes are wanted or should stay
   private, and whether external ID services are worth adding later.

#### Validation and exit gate

- Unit fixtures cover all 13 column mappings, optional blanks, ID propagation/merge, media types,
  rating validation, UTC date rollover, missing years, CSV special characters/Unicode, and stable
  column order. Use synthetic data, not exported real account data.
- Add gateway/service fixtures for a watch in two different years, ratings outside the selected
  year, rating-only titles, out-of-order episodes, gaps, specials, and incomplete/ongoing shows.
  Prove export enrichment does not change review membership, counts, or rating-date semantics.
- Integration checks cover the new format, unchanged default export, download headers, complete
  unpaged output, invalid format/year/media type, empty/header-only output, upload-size splitting,
  upstream failures, cancellation, and absence of secrets/private export contents from logs.
- Run the existing restore/build/test gates. Manually import a small synthetic movie/show file
  into a disposable Simkl account and check matching, rating/date/status/progress, blanks, and
  duplicate-import behavior. Resolve the product questions and record tested importer semantics.

**Exit gate:** approved scope, tested mapping for every populated column, documented intentional
blanks/losses, unchanged existing CSV behavior, passing automated/security gates, and a successful
manual Simkl import. Do not claim a complete watch-history migration from this CSV format.

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
